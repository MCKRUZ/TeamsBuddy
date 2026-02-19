using TeamsMeetingAssistant.Infrastructure;

namespace TeamsMeetingAssistant.Tests.Unit.Parsing;

public class VttTranscriptParserTests
{
    private static readonly VttTranscriptParser Parser = new();
    private static readonly DateTimeOffset BaseTime = new(2024, 1, 15, 9, 0, 0, TimeSpan.Zero);

    private const string SimpleVtt =
        "WEBVTT\r\n\r\n" +
        "00:00:00.000 --> 00:00:03.000\r\n" +
        "<v Alice>Good morning everyone.\r\n\r\n" +
        "00:00:05.000 --> 00:00:08.000\r\n" +
        "<v Bob>Thanks for joining.\r\n";

    [Fact]
    public void Parse_FullVtt_ReturnsCorrectSegmentCount()
    {
        var segments = Parser.Parse(SimpleVtt, BaseTime);

        Assert.Equal(2, segments.Count);
    }

    [Fact]
    public void Parse_FullVtt_FirstSegmentHasCorrectSpeakerAndContent()
    {
        var segments = Parser.Parse(SimpleVtt, BaseTime);

        Assert.Equal("Alice", segments[0].SpeakerName);
        Assert.Equal("Good morning everyone.", segments[0].Content);
    }

    [Fact]
    public void Parse_FullVtt_SecondSegmentHasCorrectSpeakerAndContent()
    {
        var segments = Parser.Parse(SimpleVtt, BaseTime);

        Assert.Equal("Bob", segments[1].SpeakerName);
        Assert.Equal("Thanks for joining.", segments[1].Content);
    }

    [Fact]
    public void Parse_FullVtt_TimestampsAreOffsetFromBaseTime()
    {
        var segments = Parser.Parse(SimpleVtt, BaseTime);

        Assert.Equal(BaseTime, segments[0].Timestamp);
        Assert.Equal(BaseTime.AddSeconds(5), segments[1].Timestamp);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmptyList()
    {
        var segments = Parser.Parse(string.Empty, BaseTime);

        Assert.Empty(segments);
    }

    [Fact]
    public void Parse_WhitespaceInput_ReturnsEmptyList()
    {
        var segments = Parser.Parse("   \n\t  ", BaseTime);

        Assert.Empty(segments);
    }

    [Fact]
    public void Parse_OnlyWebvttHeader_ReturnsEmptyList()
    {
        var segments = Parser.Parse("WEBVTT\n", BaseTime);

        Assert.Empty(segments);
    }

    [Fact]
    public void Parse_MalformedBlockWithNoSpeaker_IsSkipped()
    {
        const string vtt =
            "WEBVTT\r\n\r\n" +
            "00:00:00.000 --> 00:00:02.000\r\n" +
            "No speaker tag here\r\n\r\n" +  // no <v ...> tag
            "00:00:05.000 --> 00:00:07.000\r\n" +
            "<v Carol>This one is valid.\r\n";

        var segments = Parser.Parse(vtt, BaseTime);

        // First block has no speaker — should be skipped (HasContent = false)
        Assert.Single(segments);
        Assert.Equal("Carol", segments[0].SpeakerName);
    }

    [Fact]
    public void Parse_MultiSpeakerVtt_SeparatesSegmentsBySpeaker()
    {
        const string vtt =
            "WEBVTT\r\n\r\n" +
            "00:00:00.000 --> 00:00:03.000\r\n" +
            "<v Alice>First comment.\r\n\r\n" +
            "00:00:04.000 --> 00:00:06.000\r\n" +
            "<v Bob>Second comment.\r\n\r\n" +
            "00:00:07.000 --> 00:00:09.000\r\n" +
            "<v Alice>Third comment.\r\n";

        var segments = Parser.Parse(vtt, BaseTime);

        Assert.Equal(3, segments.Count);
        Assert.Equal("Alice", segments[0].SpeakerName);
        Assert.Equal("Bob", segments[1].SpeakerName);
        Assert.Equal("Alice", segments[2].SpeakerName);
    }

    [Fact]
    public void Parse_NoteBlocksAreSkipped()
    {
        const string vtt =
            "WEBVTT\r\n\r\n" +
            "NOTE This is a note and should be ignored\r\n\r\n" +
            "00:00:01.000 --> 00:00:03.000\r\n" +
            "<v Dave>Real content.\r\n";

        var segments = Parser.Parse(vtt, BaseTime);

        Assert.Single(segments);
        Assert.Equal("Dave", segments[0].SpeakerName);
    }

    [Fact]
    public void Parse_EachSegmentHasUniqueId()
    {
        var segments = Parser.Parse(SimpleVtt, BaseTime);

        var ids = segments.Select(s => s.Id).Distinct();
        Assert.Equal(segments.Count, ids.Count());
    }

    [Fact]
    public void Parse_SegmentStartEndTimesAreSet()
    {
        var segments = Parser.Parse(SimpleVtt, BaseTime);

        Assert.Equal(TimeSpan.Zero, segments[0].StartTime);
        Assert.Equal(TimeSpan.FromSeconds(3), segments[0].EndTime);
        Assert.Equal(TimeSpan.FromSeconds(5), segments[1].StartTime);
    }
}
