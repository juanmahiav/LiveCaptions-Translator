using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Automation;

using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public static class Translator
    {
        private static AutomationElement? window = null;
        private static Caption? caption = null;
        private static Setting? setting = null;

        private static readonly Queue<string> pendingTextQueue = new();
        private static readonly TranslationTaskQueue translationTaskQueue = new();
        private static readonly SuggestionTaskQueue suggestionTaskQueue = new();
        private static DateTime lastSpeechTime = DateTime.Now;
        private static string accumulatedText = "";
        private static readonly int PAUSE_DETECTION_MS = 1000; // N second pause detection

        public static AutomationElement? Window
        {
            get => window;
            set => window = value;
        }
        public static Caption? Caption => caption;
        public static Setting? Setting => setting;

        public static bool LogOnlyFlag { get; set; } = false;
        public static bool FirstUseFlag { get; set; } = false;

        public static event Action? TranslationLogged;

        static Translator()
        {
            window = LiveCaptionsHandler.LaunchLiveCaptions();
            LiveCaptionsHandler.FixLiveCaptions(Window);
            LiveCaptionsHandler.HideLiveCaptions(Window);

            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), models.Setting.FILENAME)))
                FirstUseFlag = true;

            caption = Caption.GetInstance();
            setting = Setting.Load();
        }

        public static void SyncLoop()
        {
            int idleCount = 0;
            int syncCount = 0;

            while (true)
            {
                if (Window == null)
                {
                    Thread.Sleep(2000);
                    continue;
                }

                string fullText = string.Empty;
                try
                {
                    // Check LiveCaptions.exe still alive
                    var info = Window.Current;
                    var name = info.Name;
                    // Get the text recognized by LiveCaptions (10-20ms)
                    fullText = LiveCaptionsHandler.GetCaptions(Window);
                }
                catch (ElementNotAvailableException)
                {
                    Window = null;
                    continue;
                }

                // Pause detection logic for suggestion mode
                if (Setting.SuggestionMode)
                {
                    if (string.IsNullOrEmpty(fullText))
                    {
                        // Check if we've had a 1-second pause
                        if ((DateTime.Now - lastSpeechTime).TotalMilliseconds >= PAUSE_DETECTION_MS)
                        {
                            // If we have accumulated text and it's been 1 second since last speech
                            if (!string.IsNullOrEmpty(accumulatedText) && accumulatedText.Length > 10)
                            {
                                // Send accumulated text for suggestion generation
                                pendingTextQueue.Enqueue(accumulatedText);
                                accumulatedText = ""; // Reset accumulated text
                            }
                        }
                    }
                    else
                    {
                        // Speech detected, update last speech time and accumulate text
                        lastSpeechTime = DateTime.Now;
                        
                        // Accumulate new text that wasn't there before
                        if (!string.IsNullOrEmpty(fullText) && !fullText.Equals(Caption.OriginalCaption, StringComparison.Ordinal))
                        {
                            accumulatedText = fullText;
                        }
                    }
                }

                if (string.IsNullOrEmpty(fullText))
                {
                    Thread.Sleep(25);
                    continue;
                }

                // Preprocess
                fullText = RegexPatterns.Acronym().Replace(fullText, "$1$2");
                fullText = RegexPatterns.AcronymWithWords().Replace(fullText, "$1 $2");
                fullText = RegexPatterns.PunctuationSpace().Replace(fullText, "$1 ");
                fullText = RegexPatterns.CJPunctuationSpace().Replace(fullText, "$1");
                // Note: For certain languages (such as Japanese), LiveCaptions excessively uses `\n`.
                // Replace redundant `\n` within sentences with comma or period.
                fullText = TextUtil.ReplaceNewlines(fullText, TextUtil.MEDIUM_THRESHOLD);

                // Prevent adding the last sentence from previous running to log cards
                // before the first sentence is completed.
                if (fullText.IndexOfAny(TextUtil.PUNC_EOS) == -1 && Caption.Contexts.Count > 0)
                {
                    Caption.Contexts.Clear();
                    Caption.OnPropertyChanged("DisplayContexts");
                }

                // Get the last sentence.
                int lastEOSIndex;
                if (Array.IndexOf(TextUtil.PUNC_EOS, fullText[^1]) != -1)
                    lastEOSIndex = fullText[0..^1].LastIndexOfAny(TextUtil.PUNC_EOS);
                else
                    lastEOSIndex = fullText.LastIndexOfAny(TextUtil.PUNC_EOS);
                string latestCaption = fullText.Substring(lastEOSIndex + 1);

                // If the last sentence is too short, extend it by adding the previous sentence.
                // Note: LiveCaptions may generate multiple characters including EOS at once.
                if (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < TextUtil.SHORT_THRESHOLD)
                {
                    lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                    latestCaption = fullText.Substring(lastEOSIndex + 1);
                }

                // `OverlayOriginalCaption`: The sentence to be displayed on Overlay Window.
                Caption.OverlayOriginalCaption = latestCaption;
                for (int historyCount = Math.Min(Setting.OverlayWindow.HistoryMax, Caption.Contexts.Count);
                     historyCount > 0 && lastEOSIndex > 0;
                     historyCount--)
                {
                    lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                    Caption.OverlayOriginalCaption = fullText.Substring(lastEOSIndex + 1);
                }
                // Caption.DisplayOriginalCaption =
                //     TextUtil.ShortenDisplaySentence(Caption.OverlayOriginalCaption, TextUtil.VERYLONG_THRESHOLD);

                // `DisplayOriginalCaption`: The sentence to be displayed on Main Window.
                if (string.CompareOrdinal(Caption.DisplayOriginalCaption, latestCaption) != 0)
                {
                    Caption.DisplayOriginalCaption = latestCaption;
                    // If the last sentence is too long, truncate it when displayed.
                    Caption.DisplayOriginalCaption =
                        TextUtil.ShortenDisplaySentence(Caption.DisplayOriginalCaption, TextUtil.VERYLONG_THRESHOLD);
                }

                // Prepare for `OriginalCaption`. If Expanded, only retain the complete sentence.
                int lastEOS = latestCaption.LastIndexOfAny(TextUtil.PUNC_EOS);
                if (lastEOS != -1)
                    latestCaption = latestCaption.Substring(0, lastEOS + 1);
                // `OriginalCaption`: The sentence to be really translated.
                if (string.CompareOrdinal(Caption.OriginalCaption, latestCaption) != 0)
                {
                    Caption.OriginalCaption = latestCaption;

                    idleCount = 0;
                    if (Array.IndexOf(TextUtil.PUNC_EOS, Caption.OriginalCaption[^1]) != -1)
                    {
                        syncCount = 0;
                        pendingTextQueue.Enqueue(Caption.OriginalCaption);
                    }
                    else if (Encoding.UTF8.GetByteCount(Caption.OriginalCaption) >= TextUtil.SHORT_THRESHOLD)
                        syncCount++;
                }
                else
                    idleCount++;

                // `TranslateFlag` determines whether this sentence should be translated.
                // When `OriginalCaption` remains unchanged, `idleCount` +1; when `OriginalCaption` changes, `MaxSyncInterval` +1.
                if (syncCount > Setting.MaxSyncInterval ||
                    idleCount == Setting.MaxIdleInterval)
                {
                    syncCount = 0;
                    pendingTextQueue.Enqueue(Caption.OriginalCaption);
                }

                Thread.Sleep(25);
            }
        }

        public static async Task TranslateLoop()
        {
            while (true)
            {
                // Check LiveCaptions.exe still alive
                if (Window == null)
                {
                    Caption.DisplayTranslatedCaption = "[WARNING] LiveCaptions was unexpectedly closed, restarting...";
                    Window = LiveCaptionsHandler.LaunchLiveCaptions();
                    Caption.DisplayTranslatedCaption = "";
                }

                // Handle suggestion mode separately from translation
                if (Setting.SuggestionMode)
                {
                    await ProcessSuggestionMode();
                }
                else
                {
                    // Normal translation mode
                    await ProcessTranslationMode();
                }

                Thread.Sleep(40);
            }
        }

        private static async Task ProcessSuggestionMode()
        {
            // In suggestion mode, handle both suggestions and regular translation queue
            if (pendingTextQueue.Count > 0)
            {
                var originalSnapshot = pendingTextQueue.Dequeue();

                if (LogOnlyFlag)
                {
                    bool isOverwrite = await IsOverwrite(originalSnapshot);
                    await LogOnly(originalSnapshot, isOverwrite);
                }
                else
                {
                    // Process as suggestion using dedicated queue
                    suggestionTaskQueue.Enqueue(
                        token => GenerateSuggestionsAsync(originalSnapshot, token),
                        originalSnapshot);
                }
            }
        }

        private static async Task ProcessTranslationMode()
        {
            // Normal translation processing
            if (pendingTextQueue.Count > 0)
            {
                var originalSnapshot = pendingTextQueue.Dequeue();

                if (LogOnlyFlag)
                {
                    bool isOverwrite = await IsOverwrite(originalSnapshot);
                    await LogOnly(originalSnapshot, isOverwrite);
                }
                else
                {
                    translationTaskQueue.Enqueue(token => Task.Run(
                        () => Translate(originalSnapshot, token), token), originalSnapshot);
                }
            }
        }

        public static async Task DisplayLoop()
        {
            while (true)
            {
                if (Setting.SuggestionMode)
                {
                    // Handle suggestion mode display
                    await DisplaySuggestionLoop();
                }
                else
                {
                    // Handle normal translation mode display
                    await DisplayTranslationLoop();
                }

                Thread.Sleep(40);
            }
        }

        private static async Task DisplaySuggestionLoop()
        {
            // In suggestion mode, update suggestions from the dedicated queue
            string currentSuggestions = suggestionTaskQueue.CurrentSuggestions;
            
            if (!string.IsNullOrEmpty(currentSuggestions) && 
                string.CompareOrdinal(Caption.ConversationSuggestions, currentSuggestions) != 0)
            {
                Caption.ConversationSuggestions = currentSuggestions;
            }

            // Also handle any translation output that might exist
            var (translatedText, isChoke) = translationTaskQueue.Output;
            if (!string.IsNullOrEmpty(translatedText) && !LogOnlyFlag)
            {
                Caption.TranslatedCaption = translatedText;
                Caption.DisplayTranslatedCaption = 
                    TextUtil.ShortenDisplaySentence(Caption.TranslatedCaption, TextUtil.VERYLONG_THRESHOLD);
            }

            Thread.Sleep(40);
        }

        private static async Task DisplayTranslationLoop()
        {
            var (translatedText, isChoke) = translationTaskQueue.Output;

            if (LogOnlyFlag)
            {
                Caption.TranslatedCaption = string.Empty;
                Caption.DisplayTranslatedCaption = "[Paused]";
                Caption.OverlayTranslatedCaption = "[Paused]";
            }
            else if (!string.IsNullOrEmpty(RegexPatterns.NoticePrefix().Replace(
                         translatedText, string.Empty).Trim()) &&
                     string.CompareOrdinal(Caption.TranslatedCaption, translatedText) != 0)
            {
                // Main page
                Caption.TranslatedCaption = translatedText;
                Caption.DisplayTranslatedCaption =
                    TextUtil.ShortenDisplaySentence(Caption.TranslatedCaption, TextUtil.VERYLONG_THRESHOLD);

                // Overlay window
                if (Caption.TranslatedCaption.Contains("[ERROR]") || Caption.TranslatedCaption.Contains("[WARNING]"))
                    Caption.OverlayTranslatedCaption = Caption.TranslatedCaption;
                else
                {
                    var match = RegexPatterns.NoticePrefixAndTranslation().Match(Caption.TranslatedCaption);
                    string noticePrefix = match.Groups[1].Value;
                    string translation = match.Groups[2].Value;
                    Caption.OverlayTranslatedCaption = noticePrefix + Caption.OverlayPreviousTranslation + translation;
                    // Caption.OverlayTranslatedCaption =
                    //     TextUtil.ShortenDisplaySentence(Caption.OverlayTranslatedCaption, TextUtil.VERYLONG_THRESHOLD);
                }
            }

            // If the original sentence is a complete sentence, choke for better visual experience.
            if (isChoke)
                Thread.Sleep(720);
        }

        public static async Task<(string, bool)> Translate(string text, CancellationToken token = default)
        {
            string translatedText;
            bool isChoke = false;
            
            try
            {
                var sw = Setting.MainWindow.LatencyShow ? Stopwatch.StartNew() : null;
                
                if (Setting.SuggestionMode)
                {
                    // In suggestion mode, generate conversation suggestions instead of translation
                    translatedText = await GenerateSuggestions(text, token);
                    isChoke = true; // Always choke for suggestions to ensure they're displayed
                }
                else
                {
                    // Normal translation mode
                    isChoke = Array.IndexOf(TextUtil.PUNC_EOS, text[^1]) != -1;
                    
                    if (Setting.ContextAware && !TranslateAPI.IsLLMBased)
                    {
                        translatedText = await TranslateAPI.TranslateFunction($"{Caption.ContextPreviousCaption} <[{text}]>", token);
                        translatedText = RegexPatterns.TargetSentence().Match(translatedText).Groups[1].Value;
                    }
                    else
                    {
                        translatedText = await TranslateAPI.TranslateFunction(text, token);
                        translatedText = translatedText.Replace("🔤", "");
                    }
                }
                
                if (sw != null)
                {
                    sw.Stop();
                    translatedText = $"[{sw.ElapsedMilliseconds} ms] " + translatedText;
                }
            }
            catch (OperationCanceledException ex)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Translation/Suggestion Failed: {ex.Message}");
                return ($"[ERROR] Translation/Suggestion Failed: {ex.Message}", isChoke);
            }

            return (translatedText, isChoke);
        }

        public static async Task<string> GenerateSuggestions(string conversationText, CancellationToken token = default)
        {
            try
            {
                // Create a prompt for generating conversation suggestions
                string suggestionPrompt = $"Based on this conversation context: \"{conversationText}\", " +
                    "provide exactly 3 brief and natural conversation suggestions to continue the dialogue. " +
                    "Format them as a numbered list (1., 2., 3.) and keep each suggestion under 10 words. " +
                    "Make suggestions friendly and relevant for gaming conversations, especially for Arc Raiders, a third-person extraction shooter where players team up to explore a post-apocalyptic Earth, fight against hostile robots called the 'ARC,' and collect loot, with the risk of losing everything if they are defeated, a player-versus-environment-versus-player (PvEvP) game." +
                    "Focus on tactical communication, team coordination, or casual gaming chat.";

                // Use the LLM API directly to generate suggestions (not through translation)
                string suggestions;
                
                if (TranslateAPI.IsLLMBased)
                {
                    // For LLM-based APIs, send the prompt directly as a system message
                    suggestions = await GenerateSuggestionsWithLLM(suggestionPrompt, token);
                }
                else
                {
                    // For non-LLM APIs, we can't generate suggestions, so return a message
                    suggestions = "[ERROR] Suggestions require an LLM-based API (OpenAI, Ollama, or OpenRouter)";
                }
                
                // Store suggestions in the Caption for display
                Caption.ConversationSuggestions = suggestions;
                
                return suggestions;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Suggestion Generation Failed: {ex.Message}");
                return "[ERROR] Could not generate suggestions";
            }
        }

        public static async Task<string> GenerateSuggestionsAsync(string conversationText, CancellationToken token = default)
        {
            try
            {
                // Create a prompt for generating conversation suggestions
                string suggestionPrompt = $"Based on this conversation context: \"{conversationText}\", " +
                    "provide exactly 3 brief and natural conversation suggestions to continue the dialogue. " +
                    "Format them as a numbered list (1., 2., 3.) and keep each suggestion under 10 words. " +
                    "Make suggestions friendly and relevant for gaming conversations, especially for Arc Raiders, a third-person extraction shooter where players team up to explore a post-apocalyptic Earth, fight against hostile robots called the 'ARC,' and collect loot, with the risk of losing everything if they are defeated, a player-versus-environment-versus-player (PvEvP) game." +
                    "Focus on tactical communication, team coordination, or casual gaming chat.";

                // Use the LLM API directly to generate suggestions (not through translation)
                string suggestions;
                
                if (TranslateAPI.IsLLMBased)
                {
                    // For LLM-based APIs, send the prompt directly as a system message
                    suggestions = await GenerateSuggestionsWithLLM(suggestionPrompt, token);
                }
                else
                {
                    // For non-LLM APIs, provide a fallback message
                    suggestions = "[INFO] Suggestions require an LLM-based API (OpenAI, Ollama, or OpenRouter). Please switch to an LLM-based API in settings to enable suggestions.";
                }
                
                // Update the caption with suggestions
                Caption.ConversationSuggestions = suggestions;
                
                return suggestions;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Suggestion Generation Failed: {ex.Message}");
                return $"[ERROR] Could not generate suggestions: {ex.Message}";
            }
        }

        public static async Task<string> GenerateSuggestionsWithLLM(string prompt, CancellationToken token = default)
        {
            // Create messages for LLM with the prompt as user input
            var messages = new List<BaseLLMConfig.Message>
            {
                new BaseLLMConfig.Message { role = "user", content = prompt }
            };

            // Use the appropriate LLM API directly
            string apiName = Setting.ApiName;
            
            switch (apiName)
            {
                case "OpenAI":
                    return await TranslateAPI.OpenAIWithCustomMessages(messages, token);
                case "Ollama":
                    return await TranslateAPI.OllamaWithCustomMessages(messages, token);
                case "OpenRouter":
                    return await TranslateAPI.OpenRouterWithCustomMessages(messages, token);
                default:
                    return "[ERROR] Unsupported LLM API for suggestions";
            }
        }

        public static async Task Log(string originalText, string translatedText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            string targetLanguage, apiName;
            if (Setting != null)
            {
                targetLanguage = Setting.TargetLanguage;
                apiName = Setting.ApiName;
            }
            else
            {
                targetLanguage = "N/A";
                apiName = "N/A";
            }

            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, translatedText, targetLanguage, apiName);
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Logging History Failed: {ex.Message}");
            }
        }

        public static async Task LogOnly(string originalText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, "N/A", "N/A", "LogOnly");
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Logging History Failed: {ex.Message}");
            }
        }

        public static async Task AddLogCard(CancellationToken token = default)
        {
            var lastLog = await SQLiteHistoryLogger.LoadLastTranslation(token);
            if (lastLog == null)
                return;

            if (Caption?.Contexts.Count >= Setting?.MainWindow.CaptionLogMax)
                Caption.Contexts.Dequeue();
            Caption?.Contexts.Enqueue(lastLog);
            Caption?.OnPropertyChanged("DisplayContexts");
        }

        public static async Task<bool> IsOverwrite(string originalText, CancellationToken token = default)
        {
            // If this text is too similar to the last one, rewrite it when logging.
            string lastOriginalText = await SQLiteHistoryLogger.LoadLastSourceText(token);
            if (lastOriginalText == null)
                return false;
            double similarity = TextUtil.Similarity(originalText, lastOriginalText);
            return similarity > 0.66;
        }
    }
}
