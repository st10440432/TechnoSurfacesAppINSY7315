using System;
using System.Collections.Generic;
using System.Text;

namespace TechnoSurfaces.Application.Auditing;

/// <summary>
/// The user on whose behalf the current operation runs. The web layer implements
/// this from the signed-in principal; the data layer depends only on this interface.
/// </summary>
public interface ICurrentUser
{
    /// <summary>Recorded when no user is signed in, for example startup seeding.</summary>
    const string SystemUserId = "system";

    /// <summary>The Identity user id, which is also the domain AppUser id.</summary>
    string UserId { get; }
}
