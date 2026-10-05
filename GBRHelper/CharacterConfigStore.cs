using System;
using System.IO;
using System.Linq;
using System.Text;
using GBRHelper.Features;
using Newtonsoft.Json;

namespace GBRHelper;

/// <summary>本人の設定だけを読み書きする。旧共有ファイルは移行元として読み取り専用。</summary>
public sealed class CharacterConfigStore
{
    private readonly string directory, legacyFile;
    private readonly Action<string, string> replace;
    private string saved = "";
    public ulong Character { get; private set; }
    public Configuration Config { get; } = new();

    public CharacterConfigStore(string directory, string legacyFile, Action<string, string>? replace = null)
    {
        this.directory = directory;
        this.legacyFile = legacyFile;
        this.replace = replace ?? ((temp, target) => File.Move(temp, target, true));
        Config.SetSave(Save);
    }

    public string FileFor(ulong character) => Path.Combine(directory, $"character_{character}.json");

    public void Switch(ulong character)
    {
        if (character == Character) return;
        // 通常の変更は都度保存する。切替前に未保存分があれば旧本人のファイルへ確定する。
        if (character != 0) Save();
        var next = new Configuration();
        if (character != 0)
        {
            var file = FileFor(character);
            if (File.Exists(file)) next = Read(file);
            else
            {
                var source = File.Exists(legacyFile) ? legacyFile
                    : Path.Combine(Path.GetDirectoryName(legacyFile)!, "GbrVentureRelay.json");
                if (File.Exists(source)) next = ForCharacter(Read(source), character);
                Write(character, next);
            }
        }
        // 読み込み・初回保存に失敗したら現設定を差し替えない。呼び出し側は処理を止めて再試行する。
        Config.CopyFrom(next);
        Character = character;
        saved = Serialize(Config);
    }

    public void Save()
    {
        if (Character == 0) return;
        var own = ForCharacter(Config, Character);
        var text = Serialize(own);
        if (text == saved) return;
        Write(Character, own);
        saved = text;
    }

    private static Configuration Read(string path) => JsonConvert.DeserializeObject<Configuration>(File.ReadAllText(path))
        ?? throw new InvalidDataException("設定ファイルが空です");
    private static string Serialize(Configuration config) => JsonConvert.SerializeObject(config, Formatting.Indented);

    private void Write(ulong character, Configuration config)
    {
        Directory.CreateDirectory(directory);
        var target = FileFor(character);
        var temp = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = Encoding.UTF8.GetBytes(Serialize(config));
                stream.Write(bytes);
                stream.Flush(true);
            }
            replace(temp, target);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static Configuration ForCharacter(Configuration source, ulong character)
    {
        var copy = JsonConvert.DeserializeObject<Configuration>(Serialize(source))!;
        copy.GatherProfiles = copy.GatherProfiles.Where(x => x.Key == character).ToDictionary();
        copy.SandChoices = copy.SandChoices.Where(x => x.Key == character).ToDictionary();
        copy.CrystalChoices = copy.CrystalChoices.Where(x => x.Key == character).ToDictionary();
        var keys = Enum.GetValues<GatherProfileKind>().SelectMany(kind => Enumerable.Range(0, 20)
            .Select(key => GatherProfiles.Name(kind, key, character))).ToHashSet();
        copy.GatherListMemory = copy.GatherListMemory.Where(x => keys.Contains(x.Key)).ToDictionary();
        copy.GatherListReset.IntersectWith(keys);
        copy.GatherListAddedDisabled.IntersectWith(keys);
        if (!copy.TimedRecoveryTag.Contains($"[Character:{character}]", StringComparison.Ordinal))
        {
            copy.TimedRecoveryTag = "";
            copy.TimedOriginalSettings.Clear();
            copy.TimedWrittenSettings.Clear();
        }
        return copy;
    }
}
