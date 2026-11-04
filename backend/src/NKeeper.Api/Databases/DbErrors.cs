using System.Data.Common;
using AdoNetCore.AseClient;

namespace NKeeper.Api.Databases;

/// <summary>Errors a database server returned (as opposed to bugs): what a section, step or request reports to the user.</summary>
internal static class DbErrors
{
    /// <summary>
    /// SqlClient's and ODP.NET's errors are <see cref="DbException"/>s; AdoNetCore.AseClient's <see cref="AseException"/>
    /// is not (it derives from SystemException), so it's named here.
    /// </summary>
    public static bool Is(Exception exception) => exception is DbException or AseException;
}
