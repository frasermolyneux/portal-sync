using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using MX.Api.Abstractions;
using Newtonsoft.Json;
using XtremeIdiots.Portal.Repository.Abstractions.Constants.V1;
using XtremeIdiots.Portal.Repository.Abstractions.Models.V1.AdminActions;
using XtremeIdiots.Portal.Repository.Api.Client.V1;
using XtremeIdiots.Portal.Sync.App.Configuration;
using XtremeIdiots.Portal.Sync.App.Repository;

namespace XtremeIdiots.Portal.Sync.App.Tests.Repository;

public class BanFilesRepositoryTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task IncompleteBanPagesCannotProduceASuccessfulInventory(bool laterPage, bool missingBody)
    {
        var client = new Mock<IRepositoryApiClient> { DefaultValue = DefaultValue.Mock };
        var api = Mock.Get(client.Object.AdminActions.V1);
        var firstBan = JsonConvert.DeserializeObject<AdminActionDto>("{}")!;
        var pages = api.SetupSequence(target => target.GetAdminActions(GameType.CallOfDuty4, null, null, AdminActionFilter.ActiveBans,
            It.IsAny<int>(), 500, AdminActionOrder.CreatedAsc, It.IsAny<CancellationToken>()));
        if (laterPage)
        {
            pages.ReturnsAsync(new ApiResult<CollectionModel<AdminActionDto>>(HttpStatusCode.OK,
                new ApiResponse<CollectionModel<AdminActionDto>>(new CollectionModel<AdminActionDto>([firstBan]))));
        }
        pages.ReturnsAsync(new ApiResult<CollectionModel<AdminActionDto>>(missingBody ? HttpStatusCode.OK : HttpStatusCode.ServiceUnavailable));
        var repository = new BanFilesRepository(NullLogger<BanFilesRepository>.Instance,
            Options.Create(new BanFilesRepositoryOptions()), client.Object);

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => repository.GetActiveBans(GameType.CallOfDuty4, default));

        Assert.Contains($"offset {(laterPage ? 500 : 0)}", error.Message);
    }
}
