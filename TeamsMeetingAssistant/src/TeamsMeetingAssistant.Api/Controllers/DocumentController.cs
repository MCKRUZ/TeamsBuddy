using Microsoft.AspNetCore.Mvc;
using TeamsMeetingAssistant.Core;
using TeamsMeetingAssistant.Core.Interfaces;

namespace TeamsMeetingAssistant.Api.Controllers;

[ApiController]
[Route("api/meetings/{meetingId}/documents")]
public class DocumentController : ControllerBase
{
    private readonly IMeetingSessionStore _sessionStore;
    private readonly IMeetingDocumentService _documentService;
    private readonly ILogger<DocumentController> _logger;

    public DocumentController(
        IMeetingSessionStore sessionStore,
        IMeetingDocumentService documentService,
        ILogger<DocumentController> logger)
    {
        _sessionStore = sessionStore;
        _documentService = documentService;
        _logger = logger;
    }

    /// <summary>
    /// Upload additional documents to an active meeting session after it has started.
    /// Files are added to the meeting's Assistants vector store.
    /// Set indexInOrgKnowledge=true to also index the document into the org-wide knowledge base.
    /// </summary>
    [HttpPost]
    [Consumes("multipart/form-data")]
    public async Task<IActionResult> UploadDocuments(
        string meetingId,
        IFormFileCollection files,
        [FromForm] bool indexInOrgKnowledge = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (files.Count == 0)
                return BadRequest(new { error = "No files provided." });

            var session = await _sessionStore.GetAsync(meetingId);
            if (session == null)
                return NotFound(new { error = "Meeting session not found." });

            if (string.IsNullOrEmpty(session.AssistantId))
                return BadRequest(new { error = "Meeting was not started with Assistants enabled." });

            var documents = ReadFormFiles(files, indexInOrgKnowledge);

            var fileIds = await _documentService.AddDocumentsAsync(
                session.AssistantId, documents, cancellationToken);

            var updatedFileIds = session.UploadedFileIds.Concat(fileIds).ToList().AsReadOnly();
            await _sessionStore.AddOrUpdateAsync(session with { UploadedFileIds = updatedFileIds });

            _logger.LogInformation(
                "Added {Count} document(s) to meeting {MeetingId}", files.Count, meetingId);

            return Ok(new { meetingId, uploadedFileIds = fileIds });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upload documents for meeting {MeetingId}", meetingId);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    private static IReadOnlyList<DocumentUpload> ReadFormFiles(IFormFileCollection files, bool indexInOrgKnowledge)
    {
        var uploads = new List<DocumentUpload>(files.Count);
        foreach (var file in files)
        {
            using var ms = new MemoryStream();
            file.CopyTo(ms);
            uploads.Add(new DocumentUpload(file.FileName, file.ContentType, ms.ToArray(), indexInOrgKnowledge));
        }
        return uploads;
    }
}
