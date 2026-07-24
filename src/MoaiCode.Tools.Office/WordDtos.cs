using System.Collections.Generic;

namespace MoaiCode.Tools.Office;

/// <summary>활성 Word 문서 스냅샷.</summary>
public sealed record WordDocInfo(
    string Name,
    string? Path,
    int ParagraphCount,
    string? SelectionText,
    IReadOnlyList<WordParaInfo> Paragraphs);

/// <summary>문단 하나. 모델이 문서 톤(크기·색·글꼴)을 보고 편집을 판단하도록 서식도 포함.</summary>
public sealed record WordParaInfo(
    int Index,
    string Text,
    string? StyleName,
    double? FontSize,
    string? FontName,
    bool? Bold,
    string? FontColor);
