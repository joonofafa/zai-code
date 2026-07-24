using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MoaiCode.Core.Messages;
using MoaiCode.Core.Tools;

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
        var target = Num("slide_index") is { } s ? $"슬라이드 {s}"
            : Num("para_index") is { } p ? $"문단 {p}"
            : !string.IsNullOrWhiteSpace(Str("cell")) ? Str("cell")
            : "현재 선택";

        var detail = Str("action") switch
        {
            "set_text" => "텍스트를 변경",
            "set_fill" => $"배경/채우기 색을 {ColorLabel(Str("color"))} 로 변경",
            "set_font" => "글자 서식을 변경",
            "set_line" => "테두리를 변경",
            "set_style" => "문단 스타일을 변경",
            "set_value" => "셀 값을 변경",
            "set_formula" => "수식을 입력",
            "insert_paragraph" => "문단을 추가",
            "delete_paragraph" => "문단을 삭제",
            "insert_table" => "표를 삽입",
            "insert_chart" => "차트를 삽입",
            "add_sheet" => "시트를 추가",
            _ => "변경",
        };

        return $"열린 문서의 {target} {detail}합니다. 적용할까요?";
    }

    private static string ColorLabel(string color) =>
        string.IsNullOrWhiteSpace(color) ? "지정한 색" : color;
}
