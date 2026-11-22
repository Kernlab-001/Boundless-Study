using BoundlessStudy.Domain.Entities;

namespace BoundlessStudy.Infrastructure.Tests;

public sealed class UnitOfWorkTests
{
    [Fact]
    public async Task Commit_makes_repository_writes_durable()
    {
        await using var db = await TestDatabase.CreateAsync();
        Guid subjectId;

        await using (var repositories = db.Store.CreateRepositories())
        {
            var profile = await repositories.LocalProfiles.FindSingleAsync();
            Assert.NotNull(profile);
            var subject = Subject.Create(profile.Id, "Committed", null, db.Clock.UtcNow);

            await repositories.UnitOfWork.BeginAsync();
            await repositories.Subjects.AddAsync(subject);
            await repositories.UnitOfWork.CommitAsync();
            subjectId = subject.Id;
        }

        await using var check = db.Store.CreateRepositories();
        Assert.NotNull(await check.Subjects.FindAsync(subjectId));
    }

    [Fact]
    public async Task Rollback_discards_every_write_and_clears_tracked_state()
    {
        await using var db = await TestDatabase.CreateAsync();
        Guid subjectId;

        await using (var repositories = db.Store.CreateRepositories())
        {
            var profile = await repositories.LocalProfiles.FindSingleAsync();
            Assert.NotNull(profile);
            var subject = Subject.Create(profile.Id, "Rolled back", null, db.Clock.UtcNow);

            await repositories.UnitOfWork.BeginAsync();
            await repositories.Subjects.AddAsync(subject);
            await repositories.UnitOfWork.RollbackAsync();
            subjectId = subject.Id;

            // The tracker was cleared, so a new subject with the same title is valid.
            var replacement = Subject.Create(profile.Id, "Rolled back", null, db.Clock.UtcNow);
            await repositories.Subjects.AddAsync(replacement);
            Assert.NotEqual(subjectId, replacement.Id);
        }

        await using var check = db.Store.CreateRepositories();
        Assert.Null(await check.Subjects.FindAsync(subjectId));
        var all = await check.Subjects.ListAsync(
            db.Store.Bootstrap!.Seed.LocalProfileId, includeArchived: true);
        Assert.Single(all);
    }

    [Fact]
    public async Task Inner_commit_does_not_make_a_nested_transaction_durable()
    {
        await using var db = await TestDatabase.CreateAsync();
        Guid subjectId;

        await using (var repositories = db.Store.CreateRepositories())
        {
            var profile = await repositories.LocalProfiles.FindSingleAsync();
            Assert.NotNull(profile);
            var subject = Subject.Create(profile.Id, "Nested", null, db.Clock.UtcNow);
            subjectId = subject.Id;

            await repositories.UnitOfWork.BeginAsync();
            await repositories.UnitOfWork.BeginAsync();
            await repositories.Subjects.AddAsync(subject);
            await repositories.UnitOfWork.CommitAsync(); // inner: depth only
            await repositories.UnitOfWork.RollbackAsync();
        }

        await using var check = db.Store.CreateRepositories();
        Assert.Null(await check.Subjects.FindAsync(subjectId));
    }

    [Fact]
    public async Task Disposing_an_open_transaction_without_commit_rolls_back()
    {
        await using var db = await TestDatabase.CreateAsync();
        Guid subjectId;

        await using (var repositories = db.Store.CreateRepositories())
        {
            var profile = await repositories.LocalProfiles.FindSingleAsync();
            Assert.NotNull(profile);
            var subject = Subject.Create(profile.Id, "Crash before commit", null, db.Clock.UtcNow);
            subjectId = subject.Id;

            await repositories.UnitOfWork.BeginAsync();
            await repositories.Subjects.AddAsync(subject);
        }

        await using var check = db.Store.CreateRepositories();
        Assert.Null(await check.Subjects.FindAsync(subjectId));
    }

    [Fact]
    public async Task Tracked_updates_persist_only_inside_a_committed_transaction()
    {
        await using var db = await TestDatabase.CreateAsync();
        Guid subjectId;

        await using (var repositories = db.Store.CreateRepositories())
        {
            var profile = await repositories.LocalProfiles.FindSingleAsync();
            Assert.NotNull(profile);
            var subject = Subject.Create(profile.Id, "Original", null, db.Clock.UtcNow);
            await repositories.Subjects.AddAsync(subject);
            subjectId = subject.Id;

            // Update-only handlers mutate the tracked entity; without an agent
            // transaction wrapper the change is not saved.
            var tracked = await repositories.Subjects.FindAsync(subjectId);
            tracked!.Rename("Dropped update", db.Clock.UtcNow);
        }

        await using (var check = db.Store.CreateRepositories())
        {
            Assert.Equal("Original", (await check.Subjects.FindAsync(subjectId))!.Name);
        }

        await using (var repositories = db.Store.CreateRepositories())
        {
            await repositories.UnitOfWork.BeginAsync();
            var tracked = await repositories.Subjects.FindAsync(subjectId);
            tracked!.Rename("Saved update", db.Clock.UtcNow);
            await repositories.UnitOfWork.CommitAsync();
        }

        await using var final = db.Store.CreateRepositories();
        Assert.Equal("Saved update", (await final.Subjects.FindAsync(subjectId))!.Name);
    }
}
