namespace ReTran_App.Services.Setup;

/// <summary>
/// Persisted intent to delete one catalog model and repair every selection that references it.
/// Intent được lưu để xoá một model catalog và sửa mọi lựa chọn tham chiếu tới model đó.
/// </summary>
/// <param name="Name">Catalog name being deleted. Tên catalog đang bị xoá.</param>
/// <param name="Installed">Catalog names expected to remain installed. Các tên catalog dự kiến còn cài.</param>
/// <param name="EngineActive">Engine-active names used for replacement preference. Các tên engine active dùng để ưu tiên model thay thế.</param>
internal sealed record ModelDeletionIntent(
    string Name,
    List<string> Installed,
    List<string> EngineActive);
