using Bridge.Contracts;
using Bridge.Data;
using Microsoft.EntityFrameworkCore;

namespace Bridge.Services;

public sealed partial class SyncService
{
    private async Task SyncMembersAsync(ProjectSync p, CancellationToken ct)
    {
        try
        {
            if (p.GitLabProject is null || p.GitLabProject.GitLabProjectId is null)
                return;

            var gitlabUsers = await _gitlab.GetGitLabProjectMembersAsync((int)p.GitLabProject.GitLabProjectId);
            var redmineUsers = await _redmine.GetRedmineMembersAsync(p.Id);

            var seen = new HashSet<(int rmId, int glId)>();
            var toAdd = new List<User>();

            foreach (var gl in gitlabUsers)
            {
                var key = ExtractSearchKey(gl.Username);

                foreach (var rm in redmineUsers)
                {
                    if (rm.Name.Contains(key, StringComparison.OrdinalIgnoreCase))
                    {
                        if (seen.Add((rm.Id, gl.Id)))
                        {
                            bool exists = await _db.Users.AnyAsync(u => u.RedmineUserId == rm.Id);
                            if (!exists)
                            {
                                toAdd.Add(new User
                                {
                                    RedmineUserId = rm.Id,
                                    GitLabUserId = gl.Id,
                                    Username = gl.Username
                                });
                            }
                        }
                    }
                }
            }

            if (toAdd.Count > 0)
            {
                _db.Users.AddRange(toAdd);
                await _db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }
}
