using System.Text.RegularExpressions;

namespace ScreenReceiver.Server.Services;

public enum QuestionType { Unknown, TextBased, GraphicBased }

/// <summary>
/// Routes a screenshot to the right expert prompt.
/// Since we send the image directly to a vision model (no separate OCR step),
/// the router returns a self-adapting prompt that tells the model to detect
/// the question type itself and apply the matching analysis framework.
/// </summary>
public static class QuestionRouterService
{
    private const string TextPrompt =
"你是银行笔试行测辅导专家。这是文字题（言语理解/数量关系/资料分析/常识判断）。\n" +
"直接输出：\n" +
"【答案】[选项字母或结果]\n" +
"【解析】[一句话说清为什么选这个，不超过80字]\n" +
"不要复述题干，不要废话；看不清就回\"题目信息缺失\"。";

    private const string GraphicPrompt =
"你是银行笔试图形推理专家。这是图形推理题。\n" +
"按以下步骤思考后输出：\n" +
"1. 看整体：元素相同/相似/凌乱？\n" +
"2. 相同→看位置（平移/旋转/翻转）；相似→看样式（去同存异/黑白叠加）；凌乱→看属性（对称/曲直/开闭），再数数量（点线面素角）。\n" +
"3. 特殊图形：日/田变形→一笔画；等腰三角形→对称性；六面体→相对面排除。\n" +
"直接输出：\n" +
"【答案】[选项字母]\n" +
"【解析】[一句话说清图形规律，不超过80字]\n" +
"不要复述题干；看不清就回\"题目信息缺失\"。";

    // Self-adapting prompt: model decides text vs graphic itself.
    // Faster than a two-pass classify-then-answer call, and works because
    // the vision model can already see the image.
    private const string AdaptivePrompt =
"你是银行笔试行测辅导专家。先判断这道题是【文字题】还是【图形推理题】，然后按对应框架回答。\n\n" +
"如果是文字题（言语理解/数量关系/资料分析/常识）：\n" +
"- 直接给答案和一句解析\n\n" +
"如果是图形推理题：\n" +
"- 元素相同看位置（平移/旋转/翻转）；相似看样式（去同存异/黑白叠加）；\n" +
"  凌乱先看属性（对称/曲直/开闭），再数数量（点线面素角）；\n" +
"- 日/田变形→一笔画；等腰三角→对称；六面体→相对面排除\n\n" +
"统一输出格式：\n" +
"【答案】[选项字母或结果]\n" +
"【解析】[一句话理由，不超过80字]\n\n" +
"不要复述题干，不要废话；看不清就回\"题目信息缺失\"。";

    /// <summary>
    /// Returns the system prompt to use. Since we have no pre-OCR text,
    /// we use the adaptive prompt that lets the vision model self-classify.
    /// </summary>
    public static string Route(byte[] imageBytes)
    {
        // Future: if we add a cheap OCR step, we can route based on detected text
        // keywords here. For now, adaptive prompt is fastest and most reliable.
        return AdaptivePrompt;
    }

    // Kept for future use when OCR text is available.
    public static (QuestionType Type, string Prompt) RouteByText(string ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
            return (QuestionType.Unknown, AdaptivePrompt);

        string[] textKeywords = { "根据以下", "阅读以下", "下列说法", "填入划横线", "同义", "反义",
            "材料", "根据上述", "这段文字", "作者", "最适合" };
        if (textKeywords.Any(k => ocrText.Contains(k)))
            return (QuestionType.TextBased, TextPrompt);

        // Very short text with only A/B/C/D markers → likely graphic
        if (ocrText.Length < 50 && Regex.IsMatch(ocrText, @"[A-D][\.、]"))
            return (QuestionType.GraphicBased, GraphicPrompt);

        return (QuestionType.Unknown, AdaptivePrompt);
    }
}
