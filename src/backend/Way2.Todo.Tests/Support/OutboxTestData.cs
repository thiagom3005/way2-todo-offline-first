using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Way2.Todo.Api.Integrations;
using Way2.Todo.Consumer;

namespace Way2.Todo.Tests.Support;

public static class OutboxTestData
{
    /// <summary>
    /// O banco é compartilhado pela coleção (testes em sequência): marca como entregues as pendências
    /// deixadas por outros testes, para que um dispatcher de teste só veja os eventos do cenário atual.
    /// </summary>
    public static async Task NeutralizePendingEventsAsync(this PostgresFixture fixture)
    {
        await using var db = fixture.CreateDbContext();
        await db.OutboxEvents.Where(e => e.Status == OutboxStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, OutboxStatuses.Delivered)
                .SetProperty(e => e.DeliveredAt, DateTime.UtcNow));
    }

    public static async Task<List<OutboxEvent>> OutboxEventsOfAsync(this PostgresFixture fixture, Guid userId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.OutboxEvents.AsNoTracking()
            .Where(e => e.UserId == userId).OrderBy(e => e.ServerRevision).ToListAsync();
    }
}

public static class TempDirectory
{
    /// <summary>Limpeza de diretório temporário de teste: melhor esforço (antivírus/indexador podem segurar arquivos).</summary>
    public static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

/// <summary>Consumidor simulado real, em memória, com log de eventos num arquivo temporário.</summary>
public sealed class ConsumerFactory(string dataPath, string secret) : WebApplicationFactory<ConsumerProgram>
{
    public string DataPath => dataPath;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Consumer:WebhookSecret", secret);
        builder.UseSetting("Consumer:DataPath", dataPath);
    }
}
