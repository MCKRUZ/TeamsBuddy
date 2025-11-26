using TeamsMeetingAssistant.Core;

namespace TeamsMeetingAssistant.Infrastructure;

public class VttTranscriptParser
{
    public List<TranscriptSegment> Parse(string vttContent, DateTimeOffset baseTime)
    {
        var segments = new List<TranscriptSegment>();

        if (string.IsNullOrWhiteSpace(vttContent))
            return segments;

        var lines = vttContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var currentSegment = new TranscriptSegmentBuilder();

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();

            // Skip WEBVTT header and empty lines
            if (trimmedLine.Equals("WEBVTT", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(trimmedLine) ||
                trimmedLine.StartsWith("NOTE"))
            {
                continue;
            }

            // Parse timestamp line: 00:00:00.000 --> 00:00:03.000
            if (trimmedLine.Contains("-->"))
            {
                // Save previous segment if it exists
                if (currentSegment.HasContent())
                {
                    segments.Add(currentSegment.Build(baseTime));
                }

                // Start new segment
                currentSegment = new TranscriptSegmentBuilder();
                var times = trimmedLine.Split("-->");
                if (times.Length == 2)
                {
                    currentSegment.SetStartTime(ParseTimeSpan(times[0].Trim()));
                    currentSegment.SetEndTime(ParseTimeSpan(times[1].Trim()));
                }
            }
            // Parse speaker line: <v Speaker Name>Text content here</v>
            else if (trimmedLine.StartsWith("<v "))
            {
                var contentStart = trimmedLine.IndexOf('>') + 1;
                if (contentStart > 0 && contentStart < trimmedLine.Length)
                {
                    var speakerWithContent = trimmedLine[3..contentStart].Trim();
                    var content = trimmedLine[contentStart..].Trim();

                    // Remove closing </v> tag if present
                    if (content.EndsWith("</v>", StringComparison.OrdinalIgnoreCase))
                    {
                        content = content[..^4].Trim();
                    }

                    // Extract full speaker name from VTT format: <v FirstName LastName>
                    // Note: VTT doesn't include Azure AD user IDs, only display names
                    // We'll use the full name as a stable identifier for role assignment
                    var speakerName = speakerWithContent.Replace(">", "").Trim();
                    
                    // Use the speaker's full name as the ID (will be mapped to actual user ID later)
                    var speakerId = speakerName;

                    currentSegment.SetSpeaker(speakerName, speakerId);
                    
                    if (!string.IsNullOrWhiteSpace(content))
                    {
                        currentSegment.AddContent(content);
                    }
                }
            }
            // Regular content line
            else if (!string.IsNullOrWhiteSpace(trimmedLine) &&
                     !trimmedLine.Contains("-->"))
            {
                var content = trimmedLine;
                
                // Remove closing </v> tag if present at the end of content line
                if (content.EndsWith("</v>", StringComparison.OrdinalIgnoreCase))
                {
                    content = content[..^4].Trim();
                }
                
                if (!string.IsNullOrWhiteSpace(content))
                {
                    currentSegment.AddContent(content);
                }
            }
        }

        // Add the last segment
        if (currentSegment.HasContent())
        {
            segments.Add(currentSegment.Build(baseTime));
        }

        return segments;
    }

    private TimeSpan ParseTimeSpan(string timeString)
    {
        // Parse format: 00:00:00.000
        var parts = timeString.Split(':');
        if (parts.Length == 3)
        {
            var hours = int.Parse(parts[0]);
            var minutes = int.Parse(parts[1]);
            var secondsAndMs = parts[2].Split('.');
            var seconds = int.Parse(secondsAndMs[0]);
            var milliseconds = secondsAndMs.Length > 1 ? int.Parse(secondsAndMs[1].PadRight(3, '0').Substring(0, 3)) : 0;

            return new TimeSpan(hours, minutes, seconds) + TimeSpan.FromMilliseconds(milliseconds);
        }

        return TimeSpan.Zero;
    }

    private class TranscriptSegmentBuilder
    {
        private string? _speakerName;
        private string? _speakerId;
        private TimeSpan _startTime;
        private TimeSpan _endTime;
        private readonly List<string> _contentParts = new();

        public void SetSpeaker(string name, string id)
        {
            _speakerName = name;
            _speakerId = id;
        }

        public void SetStartTime(TimeSpan startTime)
        {
            _startTime = startTime;
        }

        public void SetEndTime(TimeSpan endTime)
        {
            _endTime = endTime;
        }

        public void AddContent(string content)
        {
            _contentParts.Add(content);
        }

        public bool HasContent()
        {
            return !string.IsNullOrWhiteSpace(_speakerName) &&
                   _contentParts.Any();
        }

        public TranscriptSegment Build(DateTimeOffset baseTime)
        {
            var content = string.Join(" ", _contentParts);
            var timestamp = baseTime + _startTime;
            var id = Guid.NewGuid().ToString();

            return new TranscriptSegment(
                id,
                _speakerName ?? "Unknown",
                _speakerId ?? "unknown",
                content,
                timestamp,
                _startTime,
                _endTime
            );
        }
    }
}