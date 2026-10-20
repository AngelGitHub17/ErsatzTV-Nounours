using System.Text.Json;

namespace ErsatzTV.Plugin.Exemple;

public sealed record Note(DateTimeOffset When, string Text);

/// <summary>Stocke les notes dans un fichier JSON à côté du module.</summary>
public sealed class NotesStore
{
    private readonly string _file = Path.Combine(Path.GetDirectoryName(typeof(NotesStore).Assembly.Location)!, "notes.json");
    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<List<Note>> GetAll()
    {
        await _lock.WaitAsync();
        try
        {
            return File.Exists(_file)
                ? JsonSerializer.Deserialize<List<Note>>(await File.ReadAllTextAsync(_file)) ?? []
                : [];
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task Add(string text)
    {
        List<Note> notes = await GetAll();
        notes.Insert(0, new Note(DateTimeOffset.Now, text));
        await _lock.WaitAsync();
        try
        {
            await File.WriteAllTextAsync(_file, JsonSerializer.Serialize(notes.Take(200)));
        }
        finally
        {
            _lock.Release();
        }
    }
}
