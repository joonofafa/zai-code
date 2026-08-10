using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;
using MoaiCode.Localization;

namespace MoaiCode.Gui.Agent;

/// <summary>
/// GUI 권한 게이트. 열린 문서를 실제로 바꾸는 라이브 COM 편집 도구는 실행 직전
/// 사용자에게 "적용/취소" 를 확인받는다(미리보기 게이트). 그 외(문서 생성·파일 등)는 자동 승인.
/// 확인 UI 표시는 생성자로 받은 콜백(=MainViewModel.RequestConfirmAsync)에 위임한다.
/// </summary>
public sealed class GuiConfirmGate : IPermissionGate
{
    private readonly Func<string, Task<bool>> _confirm;

    public GuiConfirmGate(Func<string, Task<bool>> confirm) => _confirm = confirm;

    public async ValueTask<bool> AllowAsync(ITool tool, ToolUseBlock call, CancellationToken ct)
    {
        if (!IsLiveEdit(tool.Name))
        {
            return true; // 생성/파일 등은 확인 없이 진행
        }

        return await _confirm(Summarize(call.Input)).ConfigureAwait(false);
    }

    // 열린 문서를 즉시 변경하는 COM 편집 도구만 확인 대상.
    private static bool IsLiveEdit(string toolName) =>
        toolName is "PowerPointEdit" or "WordEdit" or "ExcelEdit";

    // call.Input(JSON)에서 사람이 읽을 한 줄 요약을 만든다.
    private static string Summarize(JsonElement input)
    {
        string Str(string key) =>
            input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty
                : string.Empty;

        int? Num(string key) =>
            input.ValueKind == JsonValueKind.Object
            && input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number
                ? v.GetInt32()
                : null;

        // 대상: PowerPoint(slide_index) / Word(para_index) / Excel(cell) / 현재 선택.
        var target = Num("slide_index") is { } s ? L10n.Get("gui.confirm.targetSlideFmt", s)
            : Num("para_index") is { } p ? L10n.Get("gui.confirm.targetParaFmt", p)
            : !string.IsNullOrWhiteSpace(Str("cell")) ? Str("cell")
            : L10n.Get("gui.confirm.targetSelection");

        var detail = Str("action") switch
        {
            "set_text" => L10n.Get("gui.confirm.detSetText"),
            "set_fill" => L10n.Get("gui.confirm.detSetFillFmt", ColorLabel(Str("color"))),
            "set_font" => L10n.Get("gui.confirm.detSetFont"),
            "set_line" => L10n.Get("gui.confirm.detSetLine"),
            "set_style" => L10n.Get("gui.confirm.detSetStyle"),
            "set_value" => L10n.Get("gui.confirm.detSetValue"),
            "set_formula" => L10n.Get("gui.confirm.detSetFormula"),
            "insert_paragraph" => L10n.Get("gui.confirm.detInsertPara"),
            "delete_paragraph" => L10n.Get("gui.confirm.detDeletePara"),
            "insert_table" => L10n.Get("gui.confirm.detInsertTable"),
            "insert_chart" => L10n.Get("gui.confirm.detInsertChart"),
            "add_sheet" => L10n.Get("gui.confirm.detAddSheet"),
            _ => L10n.Get("gui.confirm.detDefault"),
        };

        return L10n.Get("gui.confirm.summaryFmt", target, detail);
    }

    private static string ColorLabel(string color) =>
        string.IsNullOrWhiteSpace(color) ? L10n.Get("gui.confirm.colorDefault") : color;
}
