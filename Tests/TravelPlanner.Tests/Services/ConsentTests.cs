using Moq;
using Xunit;
using TravelPlanner.Models;
using TravelPlanner.Resources.Strings;
using TravelPlanner.Services;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Tests
{
    /// <summary>TP-25: explicit consent (açık rıza) before AI planning and Google sign-in, remembered and withdrawable.</summary>
    public class ConsentTests
    {
        private readonly InMemoryPreferences _preferences = new();
        private readonly Mock<IDialogService> _dialogs = new();

        private ConsentService Service() => new(_preferences, _dialogs.Object);

        private void Answer(bool agree) =>
            _dialogs.Setup(d => d.ConfirmAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>())).ReturnsAsync(agree);

        [Fact]
        public async Task Consent_IsAskedOnce_AndRemembered()
        {
            Answer(true);
            var consent = Service();

            Assert.True(await consent.EnsureAsync(ConsentKind.AiPlanning));
            Assert.True(await consent.EnsureAsync(ConsentKind.AiPlanning));

            _dialogs.Verify(d => d.ConfirmAsync(AppResources.Consent_AiTitle, AppResources.Consent_AiMessage,
                                                AppResources.Consent_Accept, AppResources.Consent_Decline), Times.Once);
            Assert.True(consent.HasConsent(ConsentKind.AiPlanning));
            Assert.False(consent.HasConsent(ConsentKind.GoogleSignIn)); // each feature separately
        }

        [Fact]
        public async Task Declining_IsNotRemembered_AndAskedAgainNextTime()
        {
            Answer(false);
            var consent = Service();

            Assert.False(await consent.EnsureAsync(ConsentKind.GoogleSignIn));
            Assert.False(await consent.EnsureAsync(ConsentKind.GoogleSignIn));

            _dialogs.Verify(d => d.ConfirmAsync(AppResources.Consent_GoogleTitle, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Exactly(2));
            Assert.False(consent.HasAnyConsent);
        }

        [Fact]
        public async Task Withdrawing_AsksAgain_AndAnOlderTextVersionCountsAsNoConsent()
        {
            Answer(true);
            var consent = Service();
            await consent.EnsureAsync(ConsentKind.AiPlanning);

            consent.RevokeAll();
            Assert.False(consent.HasAnyConsent);

            _preferences.Set(ConsentService.Key(ConsentKind.AiPlanning), "2020-01-01");
            Assert.False(consent.HasConsent(ConsentKind.AiPlanning));
        }

        [Fact]
        public async Task GoogleSignIn_WithoutConsent_DoesNotStart()
        {
            var auth = new Mock<IAuthService>();
            var consent = new Mock<IConsentService>();
            consent.Setup(c => c.EnsureAsync(ConsentKind.GoogleSignIn)).ReturnsAsync(false);
            var vm = new AuthViewModel(auth.Object, new Mock<IAccountService>().Object, Fakes.Connectivity(), Fakes.Dialogs().Object, consent.Object)
            {
                CloseAction = () => Task.CompletedTask
            };

            await vm.SignInWithGoogleCommand.ExecuteAsync(null);

            auth.Verify(a => a.SignInWithGoogleAsync(), Times.Never);
        }

        [Fact]
        public async Task GoogleSignIn_WithConsent_Starts()
        {
            var auth = new Mock<IAuthService>();
            var consent = new Mock<IConsentService>();
            consent.Setup(c => c.EnsureAsync(ConsentKind.GoogleSignIn)).ReturnsAsync(true);
            var vm = new AuthViewModel(auth.Object, new Mock<IAccountService>().Object, Fakes.Connectivity(), Fakes.Dialogs().Object, consent.Object)
            {
                CloseAction = () => Task.CompletedTask
            };

            await vm.SignInWithGoogleCommand.ExecuteAsync(null);

            auth.Verify(a => a.SignInWithGoogleAsync(), Times.Once);
        }

        [Fact]
        public async Task AiPlan_WithoutConsent_SendsNothing()
        {
            var planner = new Mock<IAiPlannerService>();
            planner.Setup(p => p.GetQuotaAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new AiQuota(5, 5));
            var trips = new Mock<ITripService>();
            trips.Setup(t => t.GetByIdAsync(1)).ReturnsAsync(new Trip
            {
                Id = 1, Name = "Roma", City = "Roma", Country = "İtalya",
                StartDate = new DateTime(2026, 10, 22), EndDate = new DateTime(2026, 10, 24)
            });
            var places = new Mock<IPlaceService>();
            places.Setup(p => p.GetByTripIdAsync(1)).ReturnsAsync(new List<Place>());
            var account = new Mock<IAccountService>();
            account.SetupGet(a => a.IsSignedIn).Returns(true);
            var consent = new Mock<IConsentService>();
            consent.Setup(c => c.EnsureAsync(ConsentKind.AiPlanning)).ReturnsAsync(false);

            var vm = new AiPlannerViewModel(planner.Object, trips.Object, places.Object, account.Object,
                                            Fakes.Connectivity(online: true), Fakes.Dialogs().Object, consent.Object)
            {
                TripId = 1,
                AfterApplied = _ => Task.CompletedTask
            };
            await vm.LoadCommand.ExecuteAsync(null);

            await vm.GenerateCommand.ExecuteAsync(null);

            consent.Verify(c => c.EnsureAsync(ConsentKind.AiPlanning), Times.Once);
            planner.Verify(p => p.CreatePlanAsync(It.IsAny<AiPlanRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        }
    }
}
