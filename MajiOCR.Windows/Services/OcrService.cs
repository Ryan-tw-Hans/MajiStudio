using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MajiOCR_Windows.Models;
using MajiOCR_Windows.Verification;
using Tesseract;

namespace MajiOCR_Windows.Services;

public class OcrResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int RequestId { get; set; }
    public List<OcrCandidateItem> Candidates { get; set; } = new();
    public long LatencyMs { get; set; }
    public float MeanConfidence { get; set; }
    public string FullText { get; set; } = string.Empty;
}

public class OcrService : IDisposable
{
    private readonly string _tessdataPath;
    private readonly object _engineLock = new();
    private TesseractEngine? _engine;
    private string? _currentEngineLanguage;
    private readonly object _chiTraLock = new();
    private TesseractEngine? _chiTraEngine;
    private bool _isDisposed;

    public OcrService()
    {
        W0Verifier.EnsureNativeDllSearchPath();
        _tessdataPath = W0Verifier.FindTessdataPath();
    }

    private TesseractEngine GetOrCreateEngineLocked(string language)
    {
        if (_isDisposed)
        {
            throw new ObjectDisposedException(nameof(OcrService));
        }

        if (!OcrLanguageCatalog.IsValidCode(language))
        {
            throw new ArgumentException($"不支援或未受控的語系代碼: '{language}'", nameof(language));
        }

        if (!Directory.Exists(_tessdataPath))
        {
            throw new DirectoryNotFoundException($"Tessdata 目錄未找到: {_tessdataPath}");
        }

        ValidateTessdataFiles(language);

        if (_engine == null || _currentEngineLanguage != language)
        {
            _engine?.Dispose();
            _engine = null;
            _currentEngineLanguage = null;

            _engine = new TesseractEngine(_tessdataPath, language, EngineMode.Default);
            _currentEngineLanguage = language;
        }

        return _engine;
    }

    private void ValidateTessdataFiles(string language)
    {
        var requiredLangs = language.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var l in requiredLangs)
        {
            string modelFile = Path.Combine(_tessdataPath, $"{l}.traineddata");
            if (!File.Exists(modelFile))
            {
                throw new FileNotFoundException($"找不到語系模型檔案: {l}.traineddata (路徑: {modelFile})", modelFile);
            }
        }
    }

    private TesseractEngine? GetOrCreateChiTraEngine()
    {
        lock (_chiTraLock)
        {
            if (_chiTraEngine == null && !_isDisposed)
            {
                try
                {
                    if (Directory.Exists(_tessdataPath) &&
                        File.Exists(Path.Combine(_tessdataPath, "chi_tra.traineddata")))
                    {
                        _chiTraEngine = new TesseractEngine(_tessdataPath, "chi_tra", EngineMode.Default);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OcrService] 次要繁中模型初始化失敗 (自動回退五語辨識): {ex.GetType().Name}");
                    _chiTraEngine = null;
                }
            }
            return _chiTraEngine;
        }
    }

    public virtual Task<OcrResult> RunFullOcrAsync(byte[] imageBytes, int requestId, CancellationToken ct, string? language = null)
    {
        if (language != null && !OcrLanguageCatalog.IsValidCode(language))
        {
            return Task.FromResult(new OcrResult
            {
                Success = false,
                RequestId = requestId,
                ErrorMessage = $"不支援的 OCR 語系代碼: '{language}'。僅允許六項受控語系。"
            });
        }
        string targetLang = OcrLanguageCatalog.NormalizeCode(language);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            var sw = Stopwatch.StartNew();

            try
            {
                using var pix = Pix.LoadFromMemory(imageBytes);

                ct.ThrowIfCancellationRequested();

                var candidates = new List<OcrCandidateItem>();
                int candidateId = 1;
                float meanConfidence = 0f;
                string fullText = string.Empty;

                lock (_engineLock)
                {
                    ct.ThrowIfCancellationRequested();
                    var engine = GetOrCreateEngineLocked(targetLang);

                    ct.ThrowIfCancellationRequested();
                    using var page = engine.Process(pix, PageSegMode.Auto);
                    meanConfidence = page.GetMeanConfidence();
                    fullText = page.GetText() ?? string.Empty;

                    using var iter = page.GetIterator();
                    if (iter != null)
                    {
                        iter.Begin();
                        do
                        {
                            ct.ThrowIfCancellationRequested();
                            if (iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var rect))
                            {
                                string lineText = iter.GetText(PageIteratorLevel.TextLine)?.Trim() ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(lineText))
                                {
                                    float conf = iter.GetConfidence(PageIteratorLevel.TextLine);
                                    candidates.Add(new OcrCandidateItem
                                    {
                                        Id = candidateId++,
                                        Text = lineText,
                                        Confidence = conf,
                                        X = rect.X1,
                                        Y = rect.Y1,
                                        Width = rect.Width,
                                        Height = rect.Height,
                                        Source = "整圖辨識",
                                        RecognizedAt = DateTime.UtcNow
                                    });
                                }
                            }
                        } while (iter.Next(PageIteratorLevel.TextLine));
                    }
                }

                // Generate Traditional Chinese suggestions (lazy load chi_tra only if at least one candidate triggers ShouldReview)
                try
                {
                    TesseractEngine? chiTraEngine = null;
                    if (candidates.Any(TraditionalChineseReviewer.ShouldReview))
                    {
                        chiTraEngine = GetOrCreateChiTraEngine();
                    }

                    if (chiTraEngine != null)
                    {
                        lock (_chiTraLock)
                        {
                            foreach (var cand in candidates)
                            {
                                ct.ThrowIfCancellationRequested();
                                TraditionalChineseReviewer.GenerateTraditionalSuggestion(chiTraEngine, pix, cand, ct);
                            }
                        }
                    }
                    else
                    {
                        foreach (var cand in candidates)
                        {
                            ct.ThrowIfCancellationRequested();
                            TraditionalChineseReviewer.GenerateTraditionalSuggestion(null, pix, cand, ct);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OcrService] 繁中建議處理異常，保留原五語候選: {ex.GetType().Name}");
                }

                sw.Stop();

                return new OcrResult
                {
                    Success = true,
                    RequestId = requestId,
                    Candidates = candidates,
                    LatencyMs = sw.ElapsedMilliseconds,
                    MeanConfidence = meanConfidence,
                    FullText = fullText
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new OcrResult
                {
                    Success = false,
                    RequestId = requestId,
                    ErrorMessage = $"OCR 辨識失敗: {ex.Message}"
                };
            }
        }, ct);
    }

    public virtual Task<OcrResult> RunRegionOcrAsync(byte[] imageBytes, int rx, int ry, int rw, int rh, int requestId, CancellationToken ct, string? language = null)
    {
        if (language != null && !OcrLanguageCatalog.IsValidCode(language))
        {
            return Task.FromResult(new OcrResult
            {
                Success = false,
                RequestId = requestId,
                ErrorMessage = $"不支援的 OCR 語系代碼: '{language}'。僅允許六項受控語系。"
            });
        }
        string targetLang = OcrLanguageCatalog.NormalizeCode(language);
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();

            if (rw < 10 || rh < 10)
            {
                return new OcrResult
                {
                    Success = false,
                    RequestId = requestId,
                    ErrorMessage = "框選區域太小 (寬高需至少 10 像素)。"
                };
            }

            var sw = Stopwatch.StartNew();

            try
            {
                using var pix = Pix.LoadFromMemory(imageBytes);

                int imageW = pix.Width;
                int imageH = pix.Height;

                // Smart Padding: Add margins around tight selection to allow Tesseract layout & stroke analysis
                const int padX = 15;
                const int padY = 10;
                int x1 = Math.Clamp(rx - padX, 0, imageW - 1);
                int y1 = Math.Clamp(ry - padY, 0, imageH - 1);
                int x2 = Math.Clamp(rx + rw + padX, 0, imageW);
                int y2 = Math.Clamp(ry + rh + padY, 0, imageH);

                if (x2 <= x1 || y2 <= y1 || (x2 - x1) < 5 || (y2 - y1) < 5)
                {
                    return new OcrResult
                    {
                        Success = false,
                        RequestId = requestId,
                        ErrorMessage = "選區越界或有效區域過小。"
                    };
                }

                var roiRect = Rect.FromCoords(x1, y1, x2, y2);
                ct.ThrowIfCancellationRequested();

                var candidates = new List<OcrCandidateItem>();
                int candidateId = 1;
                float meanConfidence = 0f;
                string fullText = string.Empty;

                lock (_engineLock)
                {
                    ct.ThrowIfCancellationRequested();
                    var engine = GetOrCreateEngineLocked(targetLang);

                    // Multi-PSM cascade: For small region crops, SingleLine & SingleBlock are vastly superior to Auto
                    var psmModes = new[] { PageSegMode.SingleLine, PageSegMode.SingleBlock, PageSegMode.Auto, PageSegMode.SparseText };
                    foreach (var psm in psmModes)
                    {
                        ct.ThrowIfCancellationRequested();
                        using var page = engine.Process(pix, roiRect, psm);
                        var psmText = page.GetText()?.Trim() ?? string.Empty;

                        using var iter = page.GetIterator();
                        if (iter != null)
                        {
                            iter.Begin();
                            do
                            {
                                ct.ThrowIfCancellationRequested();
                                if (iter.TryGetBoundingBox(PageIteratorLevel.TextLine, out var rect))
                                {
                                    string lineText = iter.GetText(PageIteratorLevel.TextLine)?.Trim() ?? string.Empty;
                                    if (!string.IsNullOrWhiteSpace(lineText))
                                    {
                                        float conf = iter.GetConfidence(PageIteratorLevel.TextLine);
                                        int candX = (rect.X1 >= x1) ? rect.X1 : (rect.X1 + x1);
                                        int candY = (rect.Y1 >= y1) ? rect.Y1 : (rect.Y1 + y1);

                                        candidates.Add(new OcrCandidateItem
                                        {
                                            Id = candidateId++,
                                            Text = lineText,
                                            Confidence = conf,
                                            X = candX,
                                            Y = candY,
                                            Width = rect.Width,
                                            Height = rect.Height,
                                            Source = "局部辨識",
                                            RecognizedAt = DateTime.UtcNow
                                        });
                                    }
                                }
                            } while (iter.Next(PageIteratorLevel.TextLine));
                        }

                        if (candidates.Count > 0 || !string.IsNullOrWhiteSpace(psmText))
                        {
                            if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(psmText))
                            {
                                candidates.Add(new OcrCandidateItem
                                {
                                    Id = candidateId++,
                                    Text = psmText,
                                    Confidence = page.GetMeanConfidence(),
                                    X = x1,
                                    Y = y1,
                                    Width = x2 - x1,
                                    Height = y2 - y1,
                                    Source = "局部辨識",
                                    RecognizedAt = DateTime.UtcNow
                                });
                            }
                            meanConfidence = page.GetMeanConfidence();
                            fullText = psmText;
                            break;
                        }
                    }

                    // If still empty and language was default mixed, try chi_tra fallback
                    if (candidates.Count == 0 && targetLang == OcrLanguageCatalog.Mixed5)
                    {
                        var chiEngine = GetOrCreateChiTraEngine();
                        if (chiEngine != null)
                        {
                            foreach (var psm in new[] { PageSegMode.SingleLine, PageSegMode.SingleBlock, PageSegMode.Auto })
                            {
                                ct.ThrowIfCancellationRequested();
                                using var page = chiEngine.Process(pix, roiRect, psm);
                                var psmText = page.GetText()?.Trim() ?? string.Empty;
                                if (!string.IsNullOrWhiteSpace(psmText))
                                {
                                    candidates.Add(new OcrCandidateItem
                                    {
                                        Id = candidateId++,
                                        Text = psmText,
                                        Confidence = page.GetMeanConfidence(),
                                        X = x1,
                                        Y = y1,
                                        Width = x2 - x1,
                                        Height = y2 - y1,
                                        Source = "局部辨識(繁中)",
                                        RecognizedAt = DateTime.UtcNow
                                    });
                                    meanConfidence = page.GetMeanConfidence();
                                    fullText = psmText;
                                    break;
                                }
                            }
                        }
                    }
                }

                // Generate Traditional Chinese suggestions & adopt for Region OCR
                try
                {
                    TesseractEngine? chiTraEngine = null;
                    if (candidates.Any(TraditionalChineseReviewer.ShouldReview))
                    {
                        chiTraEngine = GetOrCreateChiTraEngine();
                    }

                    if (chiTraEngine != null)
                    {
                        lock (_chiTraLock)
                        {
                            foreach (var cand in candidates)
                            {
                                ct.ThrowIfCancellationRequested();
                                TraditionalChineseReviewer.GenerateTraditionalSuggestion(chiTraEngine, pix, cand, ct);
                                if (!string.IsNullOrWhiteSpace(cand.TraditionalSuggestion) && cand.SuggestionSource == "繁中單語複核")
                                {
                                    cand.Text = cand.TraditionalSuggestion;
                                    cand.Source = "局部辨識(繁中)";
                                }
                            }
                        }
                    }
                    else
                    {
                        foreach (var cand in candidates)
                        {
                            ct.ThrowIfCancellationRequested();
                            TraditionalChineseReviewer.GenerateTraditionalSuggestion(null, pix, cand, ct);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[OcrService] 局部繁中建議處理異常，保留原候選: {ex.GetType().Name}");
                }

                sw.Stop();

                return new OcrResult
                {
                    Success = true,
                    RequestId = requestId,
                    Candidates = candidates,
                    LatencyMs = sw.ElapsedMilliseconds,
                    MeanConfidence = meanConfidence,
                    FullText = fullText
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new OcrResult
                {
                    Success = false,
                    RequestId = requestId,
                    ErrorMessage = $"局部 OCR 辨識失敗: {ex.Message}"
                };
            }
        }, ct);
    }

    public void Dispose()
    {
        lock (_engineLock)
        {
            if (!_isDisposed)
            {
                _engine?.Dispose();
                _engine = null;
                _currentEngineLanguage = null;
                _isDisposed = true;
            }
        }
        lock (_chiTraLock)
        {
            _chiTraEngine?.Dispose();
            _chiTraEngine = null;
        }
    }
}
