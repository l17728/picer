using System.IO;
using System.Text.Json;

namespace Picer.App;

public sealed class DirectoryClassificationPreferences
{
    private readonly string _path;
    private readonly Dictionary<string,bool> _overrides;
    public DirectoryClassificationPreferences(string path){_path=path;try{_overrides=File.Exists(path)?JsonSerializer.Deserialize<Dictionary<string,bool>>(File.ReadAllText(path))??new(StringComparer.OrdinalIgnoreCase):new(StringComparer.OrdinalIgnoreCase);}catch{_overrides=new(StringComparer.OrdinalIgnoreCase);}}

    public bool IncludesDescendants(string path)
    {
        var current=Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        while(!string.IsNullOrWhiteSpace(current)){if(_overrides.TryGetValue(current,out var value))return value;var parent=Path.GetDirectoryName(current);if(string.Equals(parent,current,StringComparison.OrdinalIgnoreCase))break;current=parent??"";}
        return false;
    }
    public void Set(string path,bool includeDescendants){_overrides[Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar)]=includeDescendants;Save();}
    private void Save(){var directory=Path.GetDirectoryName(_path);if(!string.IsNullOrWhiteSpace(directory))Directory.CreateDirectory(directory);var temporary=_path+".tmp";File.WriteAllText(temporary,JsonSerializer.Serialize(_overrides,new JsonSerializerOptions{WriteIndented=true}));File.Move(temporary,_path,true);}
}
