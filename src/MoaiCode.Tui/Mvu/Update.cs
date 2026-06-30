namespace MoaiCode.Tui.Mvu;

/// <summary>순수 상태 전이 함수 (Model, Msg) → Model.</summary>
public static class Update
{
    public static Model Apply(Model m, Msg msg) => msg switch
    {
        QuitRequested => m with { Quit = true },
        SessionCleared => m,
        _ => m,
    };
}
