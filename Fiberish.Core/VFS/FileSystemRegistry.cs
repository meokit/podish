using System;
using System.Collections.Generic;

namespace Bifrost.VFS;

public static class FileSystemRegistry
{
    private static readonly Dictionary<string, FileSystemType> _registry = new();
    private static readonly object _lock = new();

    public static void Register(FileSystemType fsType)
    {
        lock (_lock)
        {
            if (_registry.ContainsKey(fsType.Name))
            {
                throw new ArgumentException($"FileSystem '{fsType.Name}' is already registered.");
            }
            _registry[fsType.Name] = fsType;
        }
    }

    public static bool TryRegister(FileSystemType fsType)
    {
        lock (_lock)
        {
            if (_registry.ContainsKey(fsType.Name))
            {
                return false;
            }
            _registry[fsType.Name] = fsType;
            return true;
        }
    }

    public static void Unregister(string name)
    {
        lock (_lock)
        {
            _registry.Remove(name);
        }
    }

    public static FileSystemType? Get(string name)
    {
        lock (_lock)
        {
            return _registry.TryGetValue(name, out var fsType) ? fsType : null;
        }
    }

    public static List<FileSystemType> GetAll()
    {
        lock (_lock)
        {
            return new List<FileSystemType>(_registry.Values);
        }
    }
}
