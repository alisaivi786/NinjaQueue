using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace NinjaQueue.EntityFrameworkCore.Data;

/// <summary>
/// One unit of work's view of the database: the <see cref="DbContext"/>, its connection and the open transaction (if any).
/// All job SQL runs as raw ADO.NET on <see cref="Connection"/> and <see cref="Transaction"/>, so it joins whatever
/// transaction the context has.
/// </summary>
/// <typeparam name="TContext">The <see cref="DbContext"/> that maps (or reaches) the job tables.</typeparam>
internal sealed class JobSession<TContext>(TContext dbContext)
    where TContext : DbContext
{
    /// <summary>The context of this unit of work.</summary>
    public TContext DbContext { get; } = dbContext;

    /// <summary>The context's connection.</summary>
    public DbConnection Connection => DbContext.Database.GetDbConnection();

    /// <summary>The context's open transaction, or null.</summary>
    public IDbContextTransaction? Transaction => DbContext.Database.CurrentTransaction;
}