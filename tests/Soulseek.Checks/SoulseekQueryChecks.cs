using RecipeSong.Core.Soulseek;

static class SoulseekQueryChecks
{
    static void Check(string actual, string expected, string message)
    {
        if (actual != expected) throw new Exception($"{message} expected '{expected}', got '{actual}'.");
    }

    public static void Run()
    {
        Check(SoulseekQuery.ForAlbum("lighthearted", "Don't Hold Back"), "lighthearted Hold Back", "Apostrophe words must be dropped, not split into 'Don t'.");
        Check(SoulseekQuery.ForAlbum("lighthearted", "Don’t Hold Back"), "lighthearted Hold Back", "Curly apostrophes must be treated the same.");
        Check(SoulseekQuery.ForAlbum("Guns N' Roses", "Appetite for Destruction"), "Guns Roses Appetite for Destruction", "Artist apostrophe words must be dropped too.");
        Check(SoulseekQuery.ForAlbum("Artist", "Don't"), "Artist Dont", "A title made only of apostrophe words keeps the letters.");
        Check(SoulseekQuery.ForAlbum("Simon & Garfunkel", "Bridge Over Troubled Water - Remastered"), "Simon Garfunkel Bridge Over Troubled Water Remastered", "Ampersands and dashes must stay separators.");
        Check(SoulseekQuery.ForAlbum("  A  ", "  B  C "), "A B C", "Spaces must collapse.");
        Check(SoulseekQuery.ForAlbum("Megasound", "Megasound"), "Megasound", "Self-titled albums must not repeat the word.");
        Check(SoulseekQuery.ForAlbum("Bad Bad Hats", "Megasound"), "Bad Hats Megasound", "Repeated words are sent once.");
        Check(SoulseekQuery.ForAlbum("Pat Lok", "CŒUR ACIDE"), "Pat Lok ACIDE", "Ligature words must be dropped (shares spell CŒUR, Cœur or Coeur).");
        Check(SoulseekQuery.ForAlbum("Beyoncé", "Renaissance"), "Beyonce Renaissance", "A part made only of accented words is folded, not dropped.");
        Check(SoulseekQuery.ForAlbum("Christine and the Queens", "Héloïse"), "Christine and the Queens Heloise", "Accented single-word titles fold.");
        Check(SoulseekQuery.ForAlbum("Sébastien Tellier", "Sexuality"), "Tellier Sexuality", "Accented words are dropped when plain words remain.");
        Check(SoulseekQuery.ForAlbum("Artist", "Cœur"), "Artist Coeur", "A title made only of accented words is folded.");
        Check(SoulseekQuery.ForAlbum("Sigur Rós", "Ágætis byrjun"), "Sigur byrjun", "Each part keeps its plain words.");
        Check(SoulseekQuery.ForAlbum("Mø", "Straße"), "Mo Strasse", "Fully accented parts fold ligatures and slashed letters.");
        Check(SoulseekQuery.ForAlbum("검정치마", "Team Baby"), "검정치마 Team Baby", "Non-Latin scripts must stay.");
        Console.WriteLine("Soulseek query checks passed.");
    }
}
