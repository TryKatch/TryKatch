using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace FlatpackApp.Infrastructure.Persistence;

public sealed class ApplicationModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) => context is ApplicationDbContext application
        ? (context.GetType(), application.ModelCompositionKey, designTime)
        : (object)(context.GetType(), designTime);
}
