namespace FileSearch.Core.Editor;

public enum EditorTargetKind
{
    Directory,  // VS Code, Rider gibi — klasör açabilir
    File        // Notepad++ gibi — sadece dosya ister, klasör verilirse anlamsız/hata
}