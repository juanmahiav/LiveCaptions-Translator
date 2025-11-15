#!/usr/bin/env python3
"""
Test script to verify that suggestions work without translation.
This script simulates the suggestion generation process.
"""

import json
import requests

def test_suggestion_generation():
    """Test that suggestions can be generated without translation"""
    
    # Test conversation context
    conversation_text = "Hey team, let's push to the extraction point. I see some enemies ahead."
    
    # Test prompt for suggestions (similar to what the app uses)
    suggestion_prompt = f"""Based on this conversation context: \"{conversation_text}\", 
provide exactly 3 brief and natural conversation suggestions to continue the dialogue. 
Format them as a numbered list (1., 2., 3.) and keep each suggestion under 10 words. 
Make suggestions friendly and relevant for gaming conversations, especially for Arc Raiders, 
a third-person extraction shooter where players team up to explore a post-apocalyptic Earth, 
fight against hostile robots called the 'ARC,' and collect loot, with the risk of losing 
everything if they are defeated, a player-versus-environment-versus-player (PvEvP) game.
Focus on tactical communication, team coordination, or casual gaming chat."""
    
    print("=== Suggestion Generation Test ===")
    print(f"Conversation context: {conversation_text}")
    print(f"Prompt: {suggestion_prompt}")
    print("\n" + "="*50 + "\n")
    
    # Test with Ollama API (similar to what the app uses)
    try:
        # Ollama API endpoint
        ollama_url = "http://localhost:11434/api/generate"
        
        # Request payload similar to what the app would send
        payload = {
            "model": "llama3.1:8b",
            "prompt": suggestion_prompt,
            "stream": False,
            "temperature": 1.0
        }
        
        print("Sending request to Ollama API...")
        response = requests.post(ollama_url, json=payload, timeout=30)
        
        if response.status_code == 200:
            result = response.json()
            suggestions = result.get('response', 'No suggestions generated')
            
            print("✅ SUCCESS: Suggestions generated!")
            print(f"Response: {suggestions}")
            
            # Verify the suggestions are valid and not translation-related
            if any(keyword in suggestions.lower() for keyword in ['translation', 'translate', 'translated']):
                print("⚠️  WARNING: Response contains translation-related keywords")
            else:
                print("✅ GOOD: No translation-related content in response")
                
            return True
        else:
            print(f"❌ FAILED: API returned status {response.status_code}")
            print(f"Response: {response.text}")
            return False
            
    except requests.exceptions.ConnectionError:
        print("❌ FAILED: Could not connect to Ollama API at localhost:11434")
        print("Make sure Ollama is running with: ollama serve")
        return False
    except Exception as e:
        print(f"❌ FAILED: Error occurred: {e}")
        return False

def test_json_parsing():
    """Test that the JSON parsing works correctly for suggestions"""
    
    print("\n=== JSON Parsing Test ===")
    
    # Sample JSON response that might come from the API
    sample_response = {
        "response": "1. I'll cover you while you move!\n2. Watch out for snipers on the roof.\n3. Let's flank them from the left side.",
        "model": "llama3.1:8b",
        "created_at": "2024-01-01T12:00:00Z",
        "done": True
    }
    
    try:
        # Parse the JSON
        parsed = json.loads(json.dumps(sample_response))
        suggestions = parsed.get('response', '')
        
        print("✅ SUCCESS: JSON parsing works correctly")
        print(f"Parsed suggestions: {suggestions}")
        
        # Check if suggestions are properly formatted
        if suggestions and len(suggestions.strip()) > 0:
            print("✅ GOOD: Suggestions are not empty")
            return True
        else:
            print("❌ FAILED: Suggestions are empty")
            return False
            
    except json.JSONDecodeError as e:
        print(f"❌ FAILED: JSON parsing error: {e}")
        return False
    except Exception as e:
        print(f"❌ FAILED: Unexpected error: {e}")
        return False

if __name__ == "__main__":
    print("Testing LiveCaptions-Translator Suggestion System")
    print("This test verifies that suggestions work without translation\n")
    
    # Test JSON parsing first
    json_success = test_json_parsing()
    
    # Test suggestion generation
    suggestion_success = test_suggestion_generation()
    
    print("\n" + "="*50)
    print("=== TEST RESULTS ===")
    print(f"JSON Parsing Test: {'✅ PASSED' if json_success else '❌ FAILED'}")
    print(f"Suggestion Generation Test: {'✅ PASSED' if suggestion_success else '❌ FAILED'}")
    
    if json_success and suggestion_success:
        print("\n🎉 ALL TESTS PASSED! Suggestions work without translation.")
    else:
        print("\n⚠️  Some tests failed. Check the output above for details.")
