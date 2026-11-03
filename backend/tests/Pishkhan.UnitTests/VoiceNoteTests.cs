using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Services;

namespace Pishkhan.UnitTests;

/// <summary>BE-2.4: which inbound messages are voice notes, and a voice note's transcript states.</summary>
public class VoiceNoteTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_audio_attachment_with_a_url_is_the_voice_note()
    {
        VoiceNotes.AudioUrl([new InboundAttachment(AttachmentTypes.Image, "https://cdn.example/a.jpg"), new InboundAttachment(AttachmentTypes.Audio, "https://lookaside.fbsbx.com/v.mp4")])
            .ShouldBe(new Uri("https://lookaside.fbsbx.com/v.mp4"));
        VoiceNotes.AudioUrl([new InboundAttachment(AttachmentTypes.Audio, null)]).ShouldBeNull();
        VoiceNotes.AudioUrl([new InboundAttachment(AttachmentTypes.Image, "https://cdn.example/a.jpg")]).ShouldBeNull();
        VoiceNotes.AudioUrl([]).ShouldBeNull();
    }

    [Fact]
    public void A_voice_note_waits_for_its_transcript_and_keeps_it()
    {
        var message = Message.Inbound(Guid.NewGuid(), Guid.NewGuid(), InboundKinds.Attachment, null, null, "mid.1", null, Now);
        message.TranscriptStatus.ShouldBeNull();
        message.AwaitTranscript();
        message.TranscriptStatus.ShouldBe(TranscriptStatuses.Pending);
        message.Transcribed("سلام، این مدل سایز ۴۰ داره؟", Now);
        message.TranscriptStatus.ShouldBe(TranscriptStatuses.Done);
        message.Transcript.ShouldBe("سلام، این مدل سایز ۴۰ داره؟");
        message.Transcribed(new string('ب', 5_000), Now);
        message.Transcript!.Length.ShouldBe(4_000);
    }

    [Fact]
    public void A_failed_transcript_is_recorded()
    {
        var message = Message.Inbound(Guid.NewGuid(), Guid.NewGuid(), InboundKinds.Attachment, null, null, "mid.2", null, Now);
        message.AwaitTranscript();
        message.TranscriptFailed(Now);
        message.TranscriptStatus.ShouldBe(TranscriptStatuses.Failed);
        message.Transcript.ShouldBeNull();
    }
}
