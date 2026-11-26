using FdeTracker.Api.Filters;
using FdeTracker.Api.Services;
using FdeTracker.Api.Services.Rag;
using Microsoft.AspNetCore.Mvc;

namespace FdeTracker.Api.Controllers;

/// <summary>The document assistant (RAG practice). Each person works on their own documents only.</summary>
[Route("api/rag"), RequireApproved]
public sealed class RagController(RagService rag) : ApiControllerBase
{
    [HttpGet("documents")]
    public async Task<IActionResult> Documents() => Ok(await rag.List(CurrentUser.Id));

    [HttpPost("documents")]
    public async Task<IActionResult> Add(AddDocRequest body) => Ok(await rag.Add(CurrentUser.Id, body));

    [HttpDelete("documents/{id:guid}")]
    public async Task<IActionResult> Remove(Guid id) { await rag.Remove(CurrentUser.Id, id); return NoContent(); }

    [HttpPost("sample")]
    public async Task<IActionResult> Sample() => Ok(await rag.LoadSample(CurrentUser.Id));

    [HttpPost("search")]
    public async Task<IActionResult> Search(RagQuery body) => Ok(await rag.Search(CurrentUser.Id, body));

    [HttpPost("ask")]
    public async Task<IActionResult> Ask(RagQuery body, CancellationToken ct) => Ok(await rag.Ask(CurrentUser.Id, body, ct));

    /// <summary>The fixed 16-question evaluation on the built-in sample documents, for one setting.</summary>
    [HttpGet("eval")]
    public IActionResult Eval([FromQuery] string strategy = Chunker.Paragraph, [FromQuery] string mode = Retriever.Hybrid, [FromQuery(Name = "top_k")] int topK = 3, [FromQuery(Name = "chunk_size")] int? size = null, [FromQuery] int? overlap = null) => Ok(RagService.Evaluate(strategy, mode, topK, size, overlap));

    /// <summary>Every strategy, mode and Top-K side by side (without the per-question rows).</summary>
    [HttpGet("eval/compare")]
    public IActionResult Compare() => Ok(RagEval.Compare().Select(r => new { r.Strategy, r.Mode, r.TopK, r.Chunks, r.Answerable, r.HitRate, r.Mrr, r.AnswerRate, r.CitationRate, r.RefusalRate }));
}
