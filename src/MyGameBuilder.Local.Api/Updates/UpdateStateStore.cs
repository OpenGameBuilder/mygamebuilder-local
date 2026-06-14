using System.Text.Json;

namespace MyGameBuilder.Local.Api.Updates;

public sealed class UpdateStateStore
{
    private static readonly JsonSerializerOptions s_jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly UpdatePaths _paths;
    private readonly Lock _gate = new();
    private UpdateStatusState? _state;

    public UpdateStateStore(UpdatePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _paths = paths;
    }

    internal UpdateStatusState Snapshot()
    {
        lock (_gate)
        {
            EnsureLoaded();
            return Clone(_state!);
        }
    }

    internal void Mutate(Action<UpdateStatusState> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        lock (_gate)
        {
            EnsureLoaded();
            mutation(_state!);
            Save();
        }
    }

    internal void SetWorking(UpdateTarget target, string message, int progressPercent = 0)
    {
        Mutate(state =>
        {
            var item = state.For(target);
            item.State = "working";
            item.ProgressPercent = Math.Clamp(progressPercent, 0, 100);
            item.Message = message;
        });
    }

    internal void SetIdle(UpdateTarget target, string? message = null, int progressPercent = 0)
    {
        Mutate(state =>
        {
            var item = state.For(target);
            item.State = "idle";
            item.ProgressPercent = Math.Clamp(progressPercent, 0, 100);
            item.Message = message;
        });
    }

    internal void SetError(UpdateTarget target, string message)
    {
        Mutate(state =>
        {
            var item = state.For(target);
            item.State = "error";
            item.ProgressPercent = 0;
            item.Message = message;
        });
    }

    private void EnsureLoaded()
    {
        if (_state is not null)
        {
            return;
        }

        try
        {
            if (File.Exists(_paths.StatePath))
            {
                var json = File.ReadAllText(_paths.StatePath);
                _state = JsonSerializer.Deserialize<UpdateStatusState>(json, s_jsonOptions);
            }
        }
        catch (IOException)
        {
            _state = null;
        }
        catch (JsonException)
        {
            _state = null;
        }

        _state ??= new UpdateStatusState();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_paths.StatePath) ?? _paths.StagingRoot);
        var tempPath = _paths.StatePath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(_state, s_jsonOptions));
        File.Move(tempPath, _paths.StatePath, overwrite: true);
    }

    private static UpdateStatusState Clone(UpdateStatusState source) =>
        JsonSerializer.Deserialize<UpdateStatusState>(JsonSerializer.Serialize(source, s_jsonOptions), s_jsonOptions) ?? new UpdateStatusState();
}
