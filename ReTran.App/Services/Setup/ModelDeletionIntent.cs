namespace ReTran_App.Services.Setup;

/// <summary>
/// Selection-only rollback data captured before a model-delete RPC; credentials and unrelated settings
/// are deliberately excluded. Dữ liệu rollback chỉ gồm lựa chọn được chụp trước RPC xoá model; cố ý
/// không chứa credential hay settings không liên quan.
/// </summary>
internal sealed record ModelDeletionRollback(
    List<string>? Dets,
    string? ActiveDet,
    List<string>? Recs,
    string? ActiveRec,
    List<string>? Langs,
    string? ActiveLang,
    List<string>? LlmModels,
    string? ActiveLlm,
    bool IsComplete,
    string OcrDet,
    string OcrRec,
    string LlmActiveModel);

/// <summary>
/// Persisted delete state machine: <c>Pending</c> records intent before the RPC, while
/// <c>DeletedConfirmed</c> records proof that the artifact is gone before selection repair.
/// Máy trạng thái xoá được lưu: <c>Pending</c> ghi intent trước RPC, còn <c>DeletedConfirmed</c>
/// ghi bằng chứng artifact đã mất trước khi sửa các lựa chọn.
/// </summary>
/// <param name="Name">Catalog name being deleted. Tên catalog đang bị xoá.</param>
/// <param name="Installed">Catalog names expected to remain installed. Các tên catalog dự kiến còn cài.</param>
/// <param name="EngineActive">Engine-active names used for replacement preference. Các tên engine active dùng để ưu tiên model thay thế.</param>
/// <param name="State">Persisted lifecycle state, never an inference from the journal's existence. Trạng thái vòng đời được lưu, không suy ra từ sự tồn tại của journal.</param>
/// <param name="Rollback">Selection/model fields before deletion, without secrets. Các trường lựa chọn/model trước khi xoá, không chứa secret.</param>
/// <param name="Kind">Catalog family (<c>ocr</c> or <c>llm</c>) used to reconcile the actual artifact. Nhóm catalog (<c>ocr</c> hoặc <c>llm</c>) dùng để reconcile artifact thật.</param>
internal sealed record ModelDeletionIntent(
    string Name,
    List<string> Installed,
    List<string> EngineActive,
    string State = "Pending",
    ModelDeletionRollback? Rollback = null,
    string? Kind = null);
