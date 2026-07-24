using System.Collections.Generic;

namespace MoaiCode.Tools.Office;

/// <summary>활성 Word 문서 스냅샷.</summary>
public sealed record WordDocInfo(
    string Name,
    string? Path,
    int ParagraphCount,
    string? SelectionText,
    IReadOnlyList<WordParaInfo> Paragraphs);

/// <summary>문단 하나(1-based 인덱스·텍스트·스타일 이름).</summary>
public sealed record WordParaInfo(int Index, string Text, string? StyleName);
