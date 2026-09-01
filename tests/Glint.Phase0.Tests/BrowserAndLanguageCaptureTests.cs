using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class BrowserAndLanguageCaptureTests
{
    [Fact]
    public void BrowserProcessesUseExpandedUiAutomationProfile()
    {
        Assert.True(UiAutomationService.IsBrowserProcess(Window("opera", @"C:\Users\admin\AppData\Local\Programs\Opera\opera.exe")));
        Assert.True(UiAutomationService.IsBrowserProcess(Window("msedge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe")));
        Assert.False(UiAutomationService.IsBrowserProcess(Window("Code", @"C:\Users\admin\AppData\Local\Programs\Microsoft VS Code\Code.exe")));
    }

    [Fact]
    public void CombineTextPreservesJapaneseAndDeduplicatesAcrossSources()
    {
        var combined = CapturePipeline.CombineText(
            "第一章 勇者の記録\n明日の会議は午後十時です。",
            "第一章 勇者の記録\r\n追加の画面テキスト");

        Assert.Equal(
            $"第一章 勇者の記録{Environment.NewLine}明日の会議は午後十時です。{Environment.NewLine}追加の画面テキスト",
            combined);
    }

    [Fact]
    public void OcrLanguageCandidatesPrioritizeUserAndCjkLanguages()
    {
        var candidates = OcrLanguageSelector.CandidateTags(
            [
                "de-DE",
                "en-US",
                "es-ES",
                "fr-FR",
                "it-IT",
                "ja-JP",
                "ko-KR",
                "pt-BR",
                "ru-RU",
                "zh-Hans-CN"
            ],
            ["en-US"],
            maxLanguages: 4);

        Assert.Equal("en-US", candidates[0]);
        Assert.Contains("ja-JP", candidates);
        Assert.Contains("zh-Hans-CN", candidates);
        Assert.Equal(4, candidates.Count);
    }

    [Fact]
    public void OcrScoringPrefersReadableJapaneseOverLatinNoise()
    {
        var japanese = "これは日本語の本文です。勇者部隊の記録を読んでいます。";
        var noisy = "(20) F (20) J (20) æ-DB4 rc:u 160384 / 17578891.24%";

        Assert.True(
            OcrLanguageSelector.ScoreRecognizedText(japanese)
            > OcrLanguageSelector.ScoreRecognizedText(noisy));
    }

    private static ForegroundWindowInfo Window(string processName, string executablePath) =>
        new(
            123,
            42,
            processName,
            executablePath,
            "Browser test",
            new(0, 0, 1200, 800),
            false,
            false,
            true,
            false,
            false,
            true,
            false);
}
