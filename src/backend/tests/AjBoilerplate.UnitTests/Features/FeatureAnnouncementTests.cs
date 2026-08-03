using AjBoilerplate.Domain.Common;
using AjBoilerplate.Domain.Features;

namespace AjBoilerplate.UnitTests.Features;

/// <summary>
/// The announcement aggregate: which routes it targets, and the invariants its factory enforces.
/// Page targeting is the whole reason the popup does not appear everywhere, so it is exercised
/// directly rather than only through the service.
/// </summary>
public sealed class FeatureAnnouncementTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("[]")]
    [InlineData("[ ]")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_page_list_matches_every_route(string? pagesJson)
    {
        var announcement = Announcement(pagesJson);

        Assert.True(announcement.Targets("/"));
        Assert.True(announcement.Targets("/reports"));
        Assert.True(announcement.Targets("/settings/profile"));
        Assert.True(announcement.Targets(null));
    }

    [Theory]
    [InlineData("/reports")]
    [InlineData("/reports/monthly")]
    [InlineData("/reports/monthly/2026")]
    [InlineData("/reports?tab=1")]
    public void A_registered_prefix_matches_itself_and_anything_below_it(string path) =>
        Assert.True(Announcement("[\"/reports\"]").Targets(path));

    [Theory]
    [InlineData("/settings")]
    [InlineData("/")]
    [InlineData("/admin/reports")]
    public void An_unrelated_route_does_not_match(string path) =>
        Assert.False(Announcement("[\"/reports\"]").Targets(path));

    [Theory]
    [InlineData("/REPORTS/monthly")]
    [InlineData("/Reports")]
    public void Prefix_matching_is_case_insensitive(string path) =>
        // A client's casing is not something an announcement's visibility should depend on.
        Assert.True(Announcement("[\"/reports\"]").Targets(path));

    [Fact]
    public void Any_one_of_several_prefixes_is_enough() =>
        Assert.True(Announcement("[\"/reports\",\"/settings\"]").Targets("/settings/profile"));

    [Fact]
    public void A_traversal_path_does_not_match_the_prefix_it_only_appears_to_start_with()
    {
        var announcement = Announcement("[\"/reports\"]");

        // Literally "/reports/../admin".StartsWith("/reports") is true — and wrong. The path resolves
        // to /admin, so a reports-scoped announcement must not fire on it.
        Assert.False(announcement.Targets("/reports/../admin"));
        Assert.False(announcement.Targets("/reports/monthly/../../admin"));

        // ...and the guard must not break the honest case it is protecting.
        Assert.True(announcement.Targets("/reports/monthly/../weekly"));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"pages\":[\"/reports\"]}")]
    [InlineData("[1,2,3]")]
    [InlineData("[")]
    [InlineData("null")]
    public void An_unreadable_page_list_degrades_to_matching_everywhere(string pagesJson)
    {
        // This lookup runs on every navigation for every signed-in user. One malformed row must not
        // turn that into a 500 across the whole application; showing an announcement too widely is a
        // cosmetic fault, and announcements carry no authority.
        var announcement = Announcement(pagesJson);

        Assert.True(announcement.Targets("/reports"));
        Assert.True(announcement.Targets("/anything/else"));
    }

    [Fact]
    public void A_null_entry_in_the_page_list_is_ignored_rather_than_matching_everything() =>
        Assert.False(Announcement("[null,\"/reports\"]").Targets("/settings"));

    [Fact]
    public void Retire_hides_the_announcement_without_touching_its_history()
    {
        var announcement = Announcement("[]");

        announcement.Retire(Now.AddDays(1));

        Assert.False(announcement.IsActive);
        Assert.Equal(Now.AddDays(1), announcement.UpdatedAt);
        // Retiring is idempotent — a second call must not re-stamp the timestamp.
        announcement.Retire(Now.AddDays(2));
        Assert.Equal(Now.AddDays(1), announcement.UpdatedAt);
    }

    [Fact]
    public void Reinstate_brings_a_retired_announcement_back()
    {
        var announcement = Announcement("[]");
        announcement.Retire(Now.AddDays(1));

        announcement.Reinstate(Now.AddDays(2));

        Assert.True(announcement.IsActive);
    }

    [Fact]
    public void Create_requires_an_id_a_key_a_title_and_a_body()
    {
        Assert.Throws<DomainException>(() => FeatureAnnouncement.Create(
            Guid.Empty, "k", "Title", null, "Body", null, null, true, 0, Now));
        Assert.Throws<DomainException>(() => FeatureAnnouncement.Create(
            Guid.NewGuid(), "  ", "Title", null, "Body", null, null, true, 0, Now));
        Assert.Throws<DomainException>(() => FeatureAnnouncement.Create(
            Guid.NewGuid(), "k", "  ", null, "Body", null, null, true, 0, Now));
        Assert.Throws<DomainException>(() => FeatureAnnouncement.Create(
            Guid.NewGuid(), "k", "Title", null, "   ", null, null, true, 0, Now));
    }

    [Fact]
    public void Create_bounds_every_stored_string()
    {
        Assert.Throws<DomainException>(() => FeatureAnnouncement.Create(
            Guid.NewGuid(), new string('k', FeatureAnnouncement.MaxKeyLength + 1), "T", null, "B", null, null, true, 0, Now));
        Assert.Throws<DomainException>(() => FeatureAnnouncement.Create(
            Guid.NewGuid(), "k", new string('t', FeatureAnnouncement.MaxTitleLength + 1), null, "B", null, null, true, 0, Now));
        Assert.Throws<DomainException>(() => FeatureAnnouncement.Create(
            Guid.NewGuid(), "k", "T", null, new string('b', FeatureAnnouncement.MaxBodyLength + 1), null, null, true, 0, Now));
        Assert.Throws<DomainException>(() => FeatureAnnouncement.Create(
            Guid.NewGuid(), "k", "T", null, "B", null, new string('p', FeatureAnnouncement.MaxPagesJsonLength + 1), true, 0, Now));
    }

    [Fact]
    public void A_blank_translation_is_stored_as_absent_rather_than_as_empty_text()
    {
        // The client falls back to English on null. Storing "" instead would render a blank title.
        var announcement = FeatureAnnouncement.Create(
            Guid.NewGuid(), "k", "Title", "   ", "Body", "", null, true, 0, Now);

        Assert.Null(announcement.TitleAr);
        Assert.Null(announcement.BodyAr);
    }

    private static FeatureAnnouncement Announcement(string? pagesJson) => FeatureAnnouncement.Create(
        Guid.NewGuid(), "sample", "Title", null, "Body", null, pagesJson, isActive: true, displayOrder: 0, Now);
}
