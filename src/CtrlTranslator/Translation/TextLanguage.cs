namespace CtrlTranslator.Translation;

/// <summary>一段文字主要是哪种文字写的。</summary>
internal enum TextScript
{
    /// <summary>拉丁字母等其他文字（英文、法文、德文……），以及判断不出来的情况。</summary>
    Other,

    Chinese,

    Japanese,

    Korean
}

/// <summary>
/// 按字符所属的文字系统粗判语种。翻译方向（SPEC §5.4）、朗读选音都用这一份判断。
/// </summary>
/// <remarks>
/// <para>
/// 以前只数"汉字占比 > 20% 就是中文"。日文里汉字常常占三四成，于是日文被当成中文、
/// 翻成了英语；朗读也会拿中文语音去念日文。现在先看有没有假名、谚文：
/// 日文必然夹着平假名或片假名，韩文是谚文 —— 这两样中文里几乎不会出现。
/// </para>
/// <para>
/// "有效字符"按非空白字符算，和以前的口径一致，所以纯中文、纯英文的判定结果不变。
/// </para>
/// </remarks>
internal static class TextLanguage
{
    public static TextScript Detect(string text)
    {
        int meaningful = 0;
        int han = 0;
        int kana = 0;
        int hangul = 0;

        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            meaningful++;
            if (IsHan(character))
            {
                han++;
            }
            else if (IsKana(character))
            {
                kana++;
            }
            else if (IsHangul(character))
            {
                hangul++;
            }
        }

        if (meaningful == 0)
        {
            return TextScript.Other;
        }

        // 假名至少两个、且占到"汉字 + 假名"的两成才算日文：中文里夹一个片假名品牌名不该改变判断，
        // 而正常的日文句子里假名通常占一半上下。
        if (kana >= 2 && kana * 5 >= han + kana && (han + kana) * 5 > meaningful)
        {
            return TextScript.Japanese;
        }

        if (hangul * 5 > meaningful)
        {
            return TextScript.Korean;
        }

        return han * 5 > meaningful ? TextScript.Chinese : TextScript.Other;
    }

    /// <summary>汉字（CJK 统一表意文字、扩展 A、兼容表意文字）。</summary>
    public static bool IsHan(char character) =>
        character is (>= '㐀' and <= '䶿') or (>= '一' and <= '鿿') or (>= '豈' and <= '﫿');

    /// <summary>平假名、片假名（含半角片假名）。</summary>
    public static bool IsKana(char character) =>
        character is (>= '぀' and <= 'ヿ') or (>= 'ㇰ' and <= 'ㇿ') or (>= 'ｦ' and <= 'ﾝ');

    /// <summary>谚文（韩文）。</summary>
    public static bool IsHangul(char character) =>
        character is (>= '가' and <= '힯') or (>= 'ᄀ' and <= 'ᇿ') or (>= '㄰' and <= '㆏');
}
