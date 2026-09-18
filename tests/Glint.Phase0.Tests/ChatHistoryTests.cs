using Glint.Phase0.Core;

namespace Glint.Phase0.Tests;

public sealed class ChatHistoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "glint-phase0-tests",
        Guid.NewGuid().ToString("N"));

    private Phase0Database Open() =>
        Phase0Database.Open(
            Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".db"),
            new DpapiKeyStore(Path.Combine(_directory, "key.bin")));

    [Fact]
    public void ThreadsOrderByLastActivityAndMessagesReadOldestFirst()
    {
        using var database = Open();
        var first = database.CreateChatThread("First question", "all", 1_000);
        var second = database.CreateChatThread("Second question", "day", 2_000);
        database.AppendChatMessage(first.Id, ChatRoles.User, "q1", "[]", 0, 1_001);
        database.AppendChatMessage(first.Id, ChatRoles.Agent, "a1", "[]", 3, 1_002);

        // Activity on the older thread moves it back on top.
        database.AppendChatMessage(first.Id, ChatRoles.User, "q2", "[]", 0, 3_000);

        var threads = database.GetRecentChatThreads();
        Assert.Equal([first.Id, second.Id], threads.Select(item => item.Id));
        Assert.Equal("day", threads.Single(item => item.Id == second.Id).Scope);

        var messages = database.GetChatMessages(first.Id);
        Assert.Equal(
            [ChatRoles.User, ChatRoles.Agent, ChatRoles.User],
            messages.Select(message => message.Role));
        Assert.Equal("a1", messages[1].Text);
        Assert.Equal(3, messages[1].ScopedCount);
    }

    [Fact]
    public void RenameUpdatesTitleAndDeleteRemovesMessages()
    {
        using var database = Open();
        var thread = database.CreateChatThread("What did I do?", "all", 1_000);
        database.AppendChatMessage(thread.Id, ChatRoles.User, "q", "[]", 0, 1_001);

        database.RenameChatThread(thread.Id, "My day", 2_000);
        Assert.Equal("My day", database.GetChatThread(thread.Id)?.Title);

        database.DeleteChatThread(thread.Id);
        Assert.Null(database.GetChatThread(thread.Id));
        Assert.Empty(database.GetChatMessages(thread.Id));
    }

    [Fact]
    public void UnknownRolesAreRejected()
    {
        using var database = Open();
        var thread = database.CreateChatThread("t", "all", 1_000);
        Assert.Throws<ArgumentException>(
            () => database.AppendChatMessage(thread.Id, "system", "x", "[]", 0, 1_001));
    }

    [Fact]
    public void ChatSchemaSurvivesAReopen()
    {
        var keyStore = new DpapiKeyStore(Path.Combine(_directory, "key.bin"));
        var databasePath = Path.Combine(_directory, "chat-memory.db");
        var threadId = string.Empty;
        using (var database = Phase0Database.Open(databasePath, keyStore))
        {
            var created = database.CreateChatThread("Persist me?", "week", 1_000);
            threadId = created.Id;
            database.AppendChatMessage(
                threadId, ChatRoles.Agent, "answer", """[{"id":"s1"}]""", 4, 1_001);
        }

        using var reopened = Phase0Database.Open(databasePath, keyStore);
        var thread = Assert.Single(reopened.GetRecentChatThreads());
        Assert.Equal(threadId, thread.Id);
        Assert.Equal("week", thread.Scope);
        var message = Assert.Single(
            reopened.GetChatMessages(threadId),
            item => item.Role == ChatRoles.Agent);
        Assert.Equal("""[{"id":"s1"}]""", message.CitationsJson);
        Assert.Equal(4, message.ScopedCount);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception)
        {
            // Best effort test cleanup.
        }
    }
}
