using CommunityToolkit.Mvvm.ComponentModel;
using STranslate.Plugin.Ocr.Gemini.View;
using STranslate.Plugin.Ocr.Gemini.ViewModel;
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows.Controls;

namespace STranslate.Plugin.Ocr.Gemini;

public class Main : ObservableObject, IOcrPlugin, ILlm
{
    private Control? _settingUi;
    private SettingsViewModel? _viewModel;
    private Settings Settings { get; set; } = null!;
    private IPluginContext Context { get; set; } = null!;

    public IEnumerable<LangEnum> SupportedLanguages => Enum.GetValues<LangEnum>();

    public bool SupportBoxPoints() => true;

    public ObservableCollection<Prompt> Prompts { get; set; } = [];

    public Prompt? SelectedPrompt
    {
        get => Prompts.FirstOrDefault(p => p.IsEnabled);
        set => SelectPrompt(value);
    }

    public void SelectPrompt(Prompt? prompt)
    {
        if (prompt == null) return;

        // 更新所有 Prompt 的 IsEnabled 状态
        foreach (var p in Prompts)
        {
            p.IsEnabled = p == prompt;
        }

        OnPropertyChanged(nameof(SelectedPrompt));

        // 保存到配置
        Settings.Prompts = [.. Prompts.Select(p => p.Clone())];
        Context.SaveSettingStorage<Settings>();
    }

    public Control GetSettingUI()
    {
        _viewModel ??= new SettingsViewModel(Context, Settings, this);
        _settingUi ??= new SettingsView { DataContext = _viewModel };
        return _settingUi;
    }

    public void Init(IPluginContext context)
    {
        Context = context;
        Settings = context.LoadSettingStorage<Settings>();

        // 加载 Prompt 列表
        Settings.Prompts.ForEach(Prompts.Add);
    }

    public void Dispose() => _viewModel?.Dispose();

    public string? GetLanguage(LangEnum langEnum) => null;

    public async Task<OcrResult> RecognizeAsync(OcrRequest request, CancellationToken cancellationToken)
    {
        UriBuilder uriBuilder = new(Settings.Url);

        // 选择模型
        var model = Settings.Model.Trim();
        model = string.IsNullOrEmpty(model) ? "gemini-flash-latest" : model;

        uriBuilder.Path = $"/v1beta/models/{model}:generateContent";
        uriBuilder.Query = $"?key={Settings.ApiKey}";

        // 处理图片数据
        var base64Str = Convert.ToBase64String(request.ImageData);
        var formatStr = "image/png";
        
        // 温度限定
        var temperature = Math.Clamp(Settings.Temperature, 0, 2);
        var thinkingBudget = (int)Math.Clamp(Settings.ThinkingBudget, -1, 24576);

        // 替换Prompt关键字并生成 Prompt
        var prompts = Prompts.FirstOrDefault(x => x.IsEnabled) ?? throw new Exception("请先完善Prompt配置");
        var messages = prompts.Clone().Items;
        messages.ToList()
            .ForEach(item =>
                item.Content = item.Content.Replace("$target", ConvertLanguage(request.Language)));

        var userPrompt = messages.LastOrDefault() ?? throw new Exception("Prompt配置为空");
        messages.Remove(userPrompt);

        // 针对坐标框请求，调整 User Prompt，指导其返回带有 box_2d 坐标的结构化结果
        var hasSizeInfo = request.PixelWidth > 0 && request.PixelHeight > 0;
        if (hasSizeInfo)
        {
            userPrompt.Content += "\nDetect and transcribe all text segments within the image. You must output the bounding box coordinates for each text segment using a normalized 0-1000 scale. The bounding box coordinates must be in [ymin, xmin, ymax, xmax] format (e.g. [100, 150, 200, 300]). Do not omit any text lines.";
        }

        var messages2 = new List<object>();
        foreach (var item in messages)
        {
            messages2.Add(new
            {
                role = item.Role,
                parts = new[]
                {
                    new { text = item.Content }
                }
            });
        }
        messages2.Add(new
        {
            role = "user",
            parts = new object[]
            {
                new
                {
                    inline_data = new
                    {
                        mime_type = formatStr,
                        data = base64Str
                    }
                },
                new
                {
                    text = userPrompt.Content
                }
            }
        });

        // 根据是否有图片高宽信息，构建 generationConfig
        object generationConfig;
        if (hasSizeInfo)
        {
            generationConfig = new
            {
                temperature,
                thinkingConfig = thinkingBudget > 0 ? new { thinkingBudget } : null,
                response_mime_type = "application/json",
                response_schema = new
                {
                    type = "ARRAY",
                    items = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            text = new { type = "STRING" },
                            box_2d = new
                            {
                                type = "ARRAY",
                                items = new { type = "INTEGER" }
                            }
                        },
                        required = new[] { "text", "box_2d" }
                    }
                }
            };
        }
        else
        {
            generationConfig = new
            {
                temperature,
                thinkingConfig = thinkingBudget > 0 ? new { thinkingBudget } : null
            };
        }

        var content = new
        {
            contents = messages2,
            generationConfig,
            safetySettings = new object[]
            {
                new
                {
                    category = "HARM_CATEGORY_HARASSMENT",
                    threshold = "BLOCK_NONE"
                },
                new
                {
                    category = "HARM_CATEGORY_HATE_SPEECH",
                    threshold = "BLOCK_NONE"
                },
                new
                {
                    category = "HARM_CATEGORY_SEXUALLY_EXPLICIT",
                    threshold = "BLOCK_NONE"
                },
                new
                {
                    category = "HARM_CATEGORY_DANGEROUS_CONTENT",
                    threshold = "BLOCK_NONE"
                }
            }
        };

        var response = await Context.HttpService.PostAsync(uriBuilder.Uri.ToString(), content, cancellationToken: cancellationToken);
        var parsedData = JsonNode.Parse(response);
        var firstCandidate = parsedData?["candidates"] is JsonArray candidates && candidates.Count > 0 ? candidates[0] : null;
        var contentNode = firstCandidate?["content"];
        var firstPart = contentNode?["parts"] is JsonArray parts && parts.Count > 0 ? parts[0] : null;
        var data = firstPart?["text"]?.ToString() ?? throw new Exception($"No data\nRaw: {response}");

        var result = new OcrResult();

        if (hasSizeInfo)
        {
            try
            {
                var jsonArray = JsonNode.Parse(data) as JsonArray;
                if (jsonArray != null)
                {
                    foreach (var node in jsonArray)
                    {
                        if (node == null) continue;
                        var text = node["text"]?.ToString();
                        var boxArray = node["box_2d"] as JsonArray;
                        if (string.IsNullOrEmpty(text) || boxArray == null || boxArray.Count < 4) continue;

                        if (int.TryParse(boxArray[0]?.ToString(), out int ymin) &&
                            int.TryParse(boxArray[1]?.ToString(), out int xmin) &&
                            int.TryParse(boxArray[2]?.ToString(), out int ymax) &&
                            int.TryParse(boxArray[3]?.ToString(), out int xmax))
                        {
                            var ocrContent = new OcrContent { Text = text };

                            // box_2d 归一化坐标在 0-1000 之间，需映射至图片真实物理像素：
                            // 顺时针依次添加：左上、右上、右下、左下 4个顶点坐标
                            float w = request.PixelWidth;
                            float h = request.PixelHeight;

                            ocrContent.BoxPoints.Add(new BoxPoint(xmin * w / 1000f, ymin * h / 1000f)); // 左上
                            ocrContent.BoxPoints.Add(new BoxPoint(xmax * w / 1000f, ymin * h / 1000f)); // 右上
                            ocrContent.BoxPoints.Add(new BoxPoint(xmax * w / 1000f, ymax * h / 1000f)); // 右下
                            ocrContent.BoxPoints.Add(new BoxPoint(xmin * w / 1000f, ymax * h / 1000f)); // 左下

                            result.OcrContents.Add(ocrContent);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // 解析结构化 JSON 失败时，降级采用传统按行切分机制
                System.Diagnostics.Debug.WriteLine($"Failed to parse structure coordinates: {ex.Message}");
                hasSizeInfo = false;
            }
        }

        // 如果未请求位置或位置解析失败/降级
        if (!hasSizeInfo || result.OcrContents.Count == 0)
        {
            result.OcrContents.Clear();
            foreach (var item in data.Split('\n').ToList().Select(item => new OcrContent { Text = item }))
            {
                if (!string.IsNullOrWhiteSpace(item.Text))
                {
                    result.OcrContents.Add(item);
                }
            }
        }

        return result;
    }

    private string ConvertLanguage(LangEnum langEnum) => langEnum switch
    {
        LangEnum.Auto => "Requires you to identify automatically",
        LangEnum.ChineseSimplified => "Simplified Chinese",
        LangEnum.ChineseTraditional => "Traditional Chinese",
        LangEnum.Cantonese => "Cantonese",
        LangEnum.English => "English",
        LangEnum.Japanese => "Japanese",
        LangEnum.Korean => "Korean",
        LangEnum.French => "French",
        LangEnum.Spanish => "Spanish",
        LangEnum.Russian => "Russian",
        LangEnum.German => "German",
        LangEnum.Italian => "Italian",
        LangEnum.Turkish => "Turkish",
        LangEnum.PortuguesePortugal => "Portuguese",
        LangEnum.PortugueseBrazil => "Portuguese",
        LangEnum.Vietnamese => "Vietnamese",
        LangEnum.Indonesian => "Indonesian",
        LangEnum.Thai => "Thai",
        LangEnum.Malay => "Malay",
        LangEnum.Arabic => "Arabic",
        LangEnum.Hindi => "Hindi",
        LangEnum.MongolianCyrillic => "Mongolian",
        LangEnum.MongolianTraditional => "Mongolian",
        LangEnum.Khmer => "Central Khmer",
        LangEnum.NorwegianBokmal => "Norwegian Bokmål",
        LangEnum.NorwegianNynorsk => "Norwegian Nynorsk",
        LangEnum.Persian => "Persian",
        LangEnum.Swedish => "Swedish",
        LangEnum.Polish => "Polish",
        LangEnum.Dutch => "Dutch",
        LangEnum.Ukrainian => "Ukrainian",
        _ => "Requires you to identify automatically"
    };
}