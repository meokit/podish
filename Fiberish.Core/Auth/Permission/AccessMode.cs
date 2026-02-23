namespace Fiberish.Auth.Permission;

[Flags]
public enum AccessMode
{
    None = 0,
    MayExec = 1,
    MayWrite = 2,
    MayRead = 4
}
