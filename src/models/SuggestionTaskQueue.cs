using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator.models
{
    public class SuggestionTaskQueue
    {
        private readonly object _lock = new object();
        private readonly List<SuggestionTask> tasks;
        private string currentSuggestions = string.Empty;
        
        public string CurrentSuggestions => currentSuggestions;
        public bool IsProcessing { get; private set; } = false;

        public SuggestionTaskQueue()
        {
            tasks = new List<SuggestionTask>();
        }

        public void Enqueue(Func<CancellationToken, Task<string>> worker, string conversationText)
        {
            // Cancel any existing suggestion tasks
            CancelAllTasks();
            
            var newSuggestionTask = new SuggestionTask(worker, conversationText, new CancellationTokenSource());
            lock (_lock)
            {
                tasks.Add(newSuggestionTask);
                IsProcessing = true;
            }
            
            // Process the suggestion task
            _ = ProcessSuggestionTask(newSuggestionTask);
        }

        private async Task ProcessSuggestionTask(SuggestionTask suggestionTask)
        {
            try
            {
                var suggestions = await suggestionTask.Task;
                
                lock (_lock)
                {
                    // Only update if this is still the most recent task
                    if (tasks.Contains(suggestionTask))
                    {
                        currentSuggestions = suggestions;
                        IsProcessing = false;
                        
                        // Remove completed task
                        tasks.Remove(suggestionTask);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Task was cancelled, ignore
                lock (_lock)
                {
                    tasks.Remove(suggestionTask);
                    if (tasks.Count == 0)
                    {
                        IsProcessing = false;
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    currentSuggestions = $"[ERROR] Suggestion Generation Failed: {ex.Message}";
                    IsProcessing = false;
                    tasks.Remove(suggestionTask);
                }
            }
        }

        public void CancelAllTasks()
        {
            lock (_lock)
            {
                foreach (var task in tasks)
                {
                    task.CTS.Cancel();
                }
                tasks.Clear();
                IsProcessing = false;
            }
        }

        public void ClearSuggestions()
        {
            lock (_lock)
            {
                currentSuggestions = string.Empty;
                CancelAllTasks();
            }
        }
    }

    public class SuggestionTask
    {
        public Task<string> Task { get; }
        public string ConversationText { get; }
        public CancellationTokenSource CTS { get; }

        public SuggestionTask(Func<CancellationToken, Task<string>> worker,
            string conversationText, CancellationTokenSource cts)
        {
            Task = worker(cts.Token);
            OriginalText = conversationText;
            CTS = cts;
        }

        public string OriginalText { get; }
    }
}
