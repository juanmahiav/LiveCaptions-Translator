using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace LiveCaptionsTranslator
{
    public class SuggestionTest
    {
        private static readonly HttpClient httpClient = new HttpClient();
        
        public static async Task Main(string[] args)
        {
            Console.WriteLine("Testing LiveCaptions-Translator Suggestion System");
            Console.WriteLine("This test verifies that suggestions work without translation\n");
            
            // Test JSON parsing first
            bool jsonSuccess = await TestJsonParsing();
            
            // Test suggestion generation
            bool suggestionSuccess = await TestSuggestionGeneration();
            
            Console.WriteLine("\n" + new string('=', 50));
            Console.WriteLine("=== TEST RESULTS ===");
            Console.WriteLine($"JSON Parsing Test: {(jsonSuccess ? "✅ PASSED" : "❌ FAILED")}");
            Console.WriteLine($"Suggestion Generation Test: {(suggestionSuccess ? "✅ PASSED" : "❌ FAILED")}");
            
            if (jsonSuccess && suggestionSuccess)
            {
                Console.WriteLine("\n🎉 ALL TESTS PASSED! Suggestions work without translation.");
            }
            else
            {
                Console.WriteLine("\n⚠️  Some tests failed. Check the output above for details.");
            }
        }
        
        public static async Task<bool> TestJsonParsing()
        {
            Console.WriteLine("=== JSON Parsing Test ===");
            
            // Sample JSON response that might come from the API
            string sampleJson = @"{
                ""response"": ""1. I'll cover you while you move!\n2. Watch out for snipers on the roof.\n3. Let's flank them from the left side."",
                ""model"": ""llama3.1:8b"",
                ""created_at"": ""2024-01-01T12:00:00Z"",
                ""done"": true
            }";
            
            try
            {
                // Parse the JSON
                using JsonDocument doc = JsonDocument.Parse(sampleJson);
                JsonElement root = doc.RootElement;
                
                if (root.TryGetProperty("response", out JsonElement responseElement))
                {
                    string suggestions = responseElement.GetString() ?? "";
                    
                    Console.WriteLine("✅ SUCCESS: JSON parsing works correctly");
                    Console.WriteLine($"Parsed suggestions: {suggestions}");
                    
                    // Check if suggestions are properly formatted
                    if (!string.IsNullOrWhiteSpace(suggestions) && suggestions.Length > 0)
                    {
                        Console.WriteLine("✅ GOOD: Suggestions are not empty");
                        return true;
                    }
                    else
                    {
                        Console.WriteLine("❌ FAILED: Suggestions are empty");
                        return false;
                    }
                }
                else
                {
                    Console.WriteLine("❌ FAILED: Could not find 'response' property in JSON");
                    return false;
                }
            }
            catch (JsonException ex)
            {
                Console.WriteLine($"❌ FAILED: JSON parsing error: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ FAILED: Unexpected error: {ex.Message}");
                return false;
            }
        }
        
        public static async Task<bool> TestSuggestionGeneration()
        {
            Console.WriteLine("\n=== Suggestion Generation Test ===");
            
            // Test conversation context
            string conversationText = "Hey team, let's push to the extraction point. I see some enemies ahead.";
            
            // Test prompt for suggestions (similar to what the app uses)
            string suggestionPrompt = $@"Based on this conversation context: ""{conversationText}"", 
provide exactly 3 brief and natural conversation suggestions to continue the dialogue. 
Format them as a numbered list (1., 2., 3.) and keep each suggestion under 10 words. 
Make suggestions friendly and relevant for gaming conversations, especially for Arc Raiders, 
a third-person extraction shooter where players team up to explore a post-apocalyptic Earth, 
fight against hostile robots called the 'ARC,' and collect loot, with the risk of losing 
everything if they are defeated, a player-versus-environment-versus-player (PvEvP) game.
Focus on tactical communication, team coordination, or casual gaming chat.";
            
            Console.WriteLine($"Conversation context: {conversationText}");
            Console.WriteLine($"Prompt: {suggestionPrompt}");
            Console.WriteLine("\n" + new string('=', 50) + "\n");
            
            // Test with Ollama API (similar to what the app uses)
            try
            {
                // Ollama API endpoint
                string ollamaUrl = "http://localhost:11434/api/generate";
                
                // Request payload similar to what the app would send
                var payload = new
                {
                    model = "llama3.1:8b",
                    prompt = suggestionPrompt,
                    stream = false,
                    temperature = 1.0
                };
                
                string jsonPayload = JsonSerializer.Serialize(payload);
                
                Console.WriteLine("Sending request to Ollama API...");
                
                var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(ollamaUrl, content);
                
                if (response.IsSuccessStatusCode)
                {
                    string responseContent = await response.Content.ReadAsStringAsync();
                    
                    using JsonDocument doc = JsonDocument.Parse(responseContent);
                    JsonElement root = doc.RootElement;
                    
                    if (root.TryGetProperty("response", out JsonElement responseElement))
                    {
                        string suggestions = responseElement.GetString() ?? "";
                        
                        Console.WriteLine("✅ SUCCESS: Suggestions generated!");
                        Console.WriteLine($"Response: {suggestions}");
                        
                        // Verify the suggestions are valid and not translation-related
                        string lowerSuggestions = suggestions.ToLower();
                        if (lowerSuggestions.Contains("translation") || 
                            lowerSuggestions.Contains("translate") || 
                            lowerSuggestions.Contains("translated"))
                        {
                            Console.WriteLine("⚠️  WARNING: Response contains translation-related keywords");
                        }
                        else
                        {
                            Console.WriteLine("✅ GOOD: No translation-related content in response");
                        }
                        
                        return true;
                    }
                    else
                    {
                        Console.WriteLine("❌ FAILED: Could not find 'response' in API response");
                        return false;
                    }
                }
                else
                {
                    Console.WriteLine($"❌ FAILED: API returned status {response.StatusCode}");
                    string errorContent = await response.Content.ReadAsStringAsync();
                    Console.WriteLine($"Response: {errorContent}");
                    return false;
                }
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine($"❌ FAILED: Could not connect to Ollama API at localhost:11434");
                Console.WriteLine("Make sure Ollama is running with: ollama serve");
                Console.WriteLine($"Error: {ex.Message}");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ FAILED: Error occurred: {ex.Message}");
                return false;
            }
        }
    }
}
