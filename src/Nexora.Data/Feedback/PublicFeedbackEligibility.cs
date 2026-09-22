using Microsoft.EntityFrameworkCore;
using Nexora.Data.Persistence;

namespace Nexora.Data.Feedback;

internal static class PublicFeedbackEligibility
{
    internal static IQueryable<ProductFeedback> Query(NexoraDbContext dbContext) =>
        dbContext.ProductFeedbacks.AsNoTracking()
            .Where(item => item.DeletedAt == null &&
                           item.Status == Nexora.Business.Feedback.FeedbackValues.Approved &&
                           item.Consent &&
                           item.Comment != null &&
                           item.Comment.Trim() != string.Empty &&
                           item.User.IsActive &&
                           item.User.DeletionRequestedAt == null &&
                           item.User.DeletedAt == null);
}
