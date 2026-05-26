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
        // https://ai.google.dev/gemini-api/docs/image-understanding?hl=zh-cn#supported-formats
        var formatStr = "image/png";/* (Singleton<ConfigHelper>.Instance.CurrentConfig?.OcrImageQuality ?? OcrImageQualityEnum.Medium) switch
        {
            OcrImageQualityEnum.Low => "image/jpeg",
            OcrImageQualityEnum.Medium => "image/png",
            _ => "image/png"//即便是bmp 使用 png 标签 gemini 也能正常识别（gemini-2.0-flash-exp）
        };*/
        // 温度限定
        var temperature = Math.Clamp(Settings.Temperature, 0, 2);

        var thinkingBudget = (int)Math.Clamp(Settings.ThinkingBudget, -1, 24576);


        // 替换Prompt关键字
        var messages = (Prompts.FirstOrDefault(x => x.IsEnabled) ?? throw new Exception("请先完善Prompt配置"))
            .Clone()
            .Items;
        messages.ToList()
            .ForEach(item =>
                item.Content = item.Content.Replace("$target", ConvertLanguage(request.Language)));

        var userPrompt = messages.LastOrDefault() ?? throw new Exception("Prompt配置为空");
        messages.Remove(userPrompt);
        var messages2 = new List<object>();
        foreach (var item in messages)
        {
            messages2.Add(new
            {
                role = item.Role,
                part = new[]
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

        var content = new
        {
            contents = messages2,
#if false
            systemInstruction = new
            {
                role = "user",
                parts = new[]
                {
                    new
                    {
                        text = "You are a specialized OCR engine that accurately extracts each text from the image."
                    }
                }
            },
            generationConfig = new
            {
                temperature = aTemperature,
                response_mime_type = "application/json",
                response_schema = new
                {
                    type = "ARRAY",
                    items = new
                    {
                        type = "OBJECT",
                        properties = new
                        {
                            words = new
                            {
                                type = "STRING"
                            },
                            location = new
                            {
                                type = "OBJECT",
                                properties = new
                                {
                                    top = new
                                    {
                                        type = "NUMBER"
                                    },
                                    left = new
                                    {
                                        type = "NUMBER"
                                    },
                                    width = new
                                    {
                                        type = "NUMBER"
                                    },
                                    height = new
                                    {
                                        type = "NUMBER"
                                    }
                                }
                            }
                        }
                    }
                }
            }, 
#endif
            generationConfig = new
            {
                temperature,
                thinkingConfig = new { thinkingBudget }
            },
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
        foreach (var item in data.Split("\n").ToList().Select(item => new OcrContent { Text = item }))
        {
            result.OcrContents.Add(item);
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