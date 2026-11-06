using RecipeSong.Core;

namespace RecipeSong.Wpf;

public enum WorkspacePage { Recommendations, Artists, Soulseek }

public sealed class WorkspaceNavigation
{
    public WorkspacePage SearchOrigin { get; private set; }
    public string BackLabel => SearchOrigin == WorkspacePage.Artists ? L.T("아티스트 목록으로") : L.T("추천으로");
    public void BeginSearch(WorkspacePage from)
    {
        if (from != WorkspacePage.Soulseek) SearchOrigin = from;
    }
}
