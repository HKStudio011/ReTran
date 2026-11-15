namespace ReTran_App.Services.Setup;

/// <summary>
/// State of one item on the setup wizard's live checklist (the Checking step).
/// Trạng thái một mục trên checklist live của setup wizard (bước Checking).
/// </summary>
public enum SetupCheckState
{
    /// <summary>Not reached yet. Chưa tới lượt.</summary>
    Pending,

    /// <summary>Currently being evaluated. Đang kiểm tra.</summary>
    Running,

    /// <summary>Passed. Đạt.</summary>
    Ok,

    /// <summary>Completed with an advisory (e.g. GPU→CPU downgrade); does not block. Xong kèm cảnh báo (vd hạ GPU→CPU); không chặn.</summary>
    Warn,

    /// <summary>Failed; the wizard moves to its Failed screen. Lỗi; wizard chuyển màn Failed.</summary>
    Fail,
}

/// <summary>
/// One progress event of <see cref="SystemCheck.Run"/>: which checklist item changed, its new state,
/// and the localizable label/detail the UI renders. The coordinator keeps the latest event per Id.
/// Một sự kiện tiến độ của <see cref="SystemCheck.Run"/>: mục nào đổi, trạng thái mới, và
/// label/detail bản địa hoá để UI render. Coordinator giữ event mới nhất theo Id.
/// </summary>
/// <param name="Id">
/// Stable id the UI replaces in place (e.g. <c>files</c>, <c>net:github.com</c>).
/// Id ổn định để UI thay mục tại chỗ (vd <c>files</c>, <c>net:github.com</c>).
/// </param>
/// <param name="State">New state of the item. Trạng thái mới của mục.</param>
/// <param name="Label">Localizable item label (i18n key + args). Nhãn mục bản địa hoá (key + args).</param>
/// <param name="Detail">Optional localizable detail line. Dòng chú thích tuỳ chọn.</param>
public sealed record SetupCheckItem(
    string Id, SetupCheckState State, SetupMessage Label, SetupMessage? Detail = null);
