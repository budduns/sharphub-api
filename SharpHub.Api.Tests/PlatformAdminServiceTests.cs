using Microsoft.EntityFrameworkCore;
using SharpHub.Api.Data;
using SharpHub.Api.Models;
using SharpHub.Api.Services;
using Xunit;

namespace SharpHub.Api.Tests.Integration
{
    /// <summary>
    /// Integration tests for Platform Admin functionality.
    /// Tests super_admin bypass and org-scoped filtering.
    /// </summary>
    public class PlatformAdminServiceTests : IAsyncLifetime
    {
        private SharpHubDbContext _dbContext;
        private PlatformAdminService _platformAdminService;
        private readonly IConfiguration _configuration;
        private readonly HttpContextAccessor _httpContextAccessor;

        public PlatformAdminServiceTests()
        {
            // Create in-memory database for testing
            var options = new DbContextOptionsBuilder<SharpHubDbContext>()
                .UseInMemoryDatabase(databaseName: $"test_db_{Guid.NewGuid()}")
                .Options;

            _dbContext = new SharpHubDbContext(options);

            // Mock configuration with bootstrap emails
            var configDict = new Dictionary<string, string>
            {
                { "SHARP_SUPER_ADMIN_EMAILS", "admin@test.com;super@test.com" }
            };
            var configBuilder = new ConfigurationBuilder()
                .AddInMemoryCollection(configDict);
            _configuration = configBuilder.Build();

            _httpContextAccessor = new HttpContextAccessor();
            _platformAdminService = new PlatformAdminService(_dbContext, _configuration, _httpContextAccessor);
        }

        public async Task InitializeAsync()
        {
            await _dbContext.Database.EnsureCreatedAsync();
        }

        public async Task DisposeAsync()
        {
            await _dbContext.Database.EnsureDeletedAsync();
            _dbContext.Dispose();
        }

        [Fact]
        public async Task IsSuperAdminAsync_WithBootstrapEmail_ReturnsTrue()
        {
            // Arrange
            var email = "admin@test.com";

            // Act
            var result = await _platformAdminService.IsSuperAdminAsync(email);

            // Assert
            Assert.True(result, "User with bootstrap email should be identified as super admin");
        }

        [Fact]
        public async Task IsSuperAdminAsync_WithNonBootstrapEmail_ReturnsFalse()
        {
            // Arrange
            var email = "regular@test.com";

            // Act
            var result = await _platformAdminService.IsSuperAdminAsync(email);

            // Assert
            Assert.False(result, "User not in bootstrap list should not be super admin");
        }

        [Fact]
        public async Task IsSuperAdminAsync_WithDatabaseEntry_ReturnsTrue()
        {
            // Arrange
            var email = "database@test.com";
            var platformRole = new PlatformRole
            {
                UserExternalId = email,
                Role = PlatformRole.RoleConstants.SuperAdmin,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsDeleted = false
            };
            _dbContext.PlatformRoles.Add(platformRole);
            await _dbContext.SaveChangesAsync();

            // Act
            var result = await _platformAdminService.IsSuperAdminAsync(email);

            // Assert
            Assert.True(result, "User with super_admin role in database should be identified as super admin");
        }

        [Fact]
        public async Task IsSuperAdminAsync_WithDeletedEntry_ReturnsFalse()
        {
            // Arrange
            var email = "deleted@test.com";
            var platformRole = new PlatformRole
            {
                UserExternalId = email,
                Role = PlatformRole.RoleConstants.SuperAdmin,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsDeleted = true  // Soft deleted
            };
            _dbContext.PlatformRoles.Add(platformRole);
            await _dbContext.SaveChangesAsync();

            // Act
            var result = await _platformAdminService.IsSuperAdminAsync(email);

            // Assert
            Assert.False(result, "Deleted platform role should not grant super admin access");
        }

        [Fact]
        public async Task EnsureSuperAdminRowAsync_CreatesNewRole()
        {
            // Arrange
            var email = "newadmin@test.com";

            // Act
            await _platformAdminService.EnsureSuperAdminRowAsync(email);

            // Assert
            var roleExists = await _dbContext.PlatformRoles
                .AnyAsync(pr =>
                    pr.UserExternalId.ToLower() == email.ToLower()
                    && pr.Role == PlatformRole.RoleConstants.SuperAdmin
                    && !pr.IsDeleted);

            Assert.True(roleExists, "EnsureSuperAdminRowAsync should create a super_admin role");
        }

        [Fact]
        public async Task EnsureSuperAdminRowAsync_DoesNotDuplicateExistingRole()
        {
            // Arrange
            var email = "admin@test.com";
            var platformRole = new PlatformRole
            {
                UserExternalId = email,
                Role = PlatformRole.RoleConstants.SuperAdmin,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsDeleted = false
            };
            _dbContext.PlatformRoles.Add(platformRole);
            await _dbContext.SaveChangesAsync();

            var initialCount = await _dbContext.PlatformRoles.CountAsync();

            // Act
            await _platformAdminService.EnsureSuperAdminRowAsync(email);

            // Assert
            var finalCount = await _dbContext.PlatformRoles.CountAsync();
            Assert.Equal(initialCount, finalCount, "EnsureSuperAdminRowAsync should not create duplicate roles");
        }

        [Fact]
        public async Task GetSuperAdminsAsync_ReturnsBothDatabaseAndBootstrapAdmins()
        {
            // Arrange
            var dbEmail = "database@test.com";
            var platformRole = new PlatformRole
            {
                UserExternalId = dbEmail,
                Role = PlatformRole.RoleConstants.SuperAdmin,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                IsDeleted = false
            };
            _dbContext.PlatformRoles.Add(platformRole);
            await _dbContext.SaveChangesAsync();

            // Act
            var admins = await _platformAdminService.GetSuperAdminsAsync();

            // Assert
            var adminList = admins.ToList();
            Assert.Contains(dbEmail, adminList);
            Assert.Contains("admin@test.com", adminList);
            Assert.Contains("super@test.com", adminList);
        }
    }

    /// <summary>
    /// Integration tests for CurrentUserService.
    /// </summary>
    public class CurrentUserServiceTests
    {
        [Fact]
        public void ExternalId_PrefersEmail()
        {
            // Arrange
            var httpContext = new DefaultHttpContext();
            var claims = new List<System.Security.Claims.Claim>
            {
                new System.Security.Claims.Claim("email", "user@test.com"),
                new System.Security.Claims.Claim("oid", "some-oid")
            };
            var identity = new System.Security.Claims.ClaimsIdentity(claims);
            var principal = new System.Security.Claims.ClaimsPrincipal(identity);
            httpContext.User = principal;

            var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
            var service = new CurrentUserService(httpContextAccessor);

            // Act
            var externalId = service.ExternalId;

            // Assert
            Assert.Equal("user@test.com", externalId);
        }

        [Fact]
        public void ExternalId_FallsbackToOid_WhenEmailMissing()
        {
            // Arrange
            var httpContext = new DefaultHttpContext();
            var claims = new List<System.Security.Claims.Claim>
            {
                new System.Security.Claims.Claim("oid", "some-oid-value")
            };
            var identity = new System.Security.Claims.ClaimsIdentity(claims);
            var principal = new System.Security.Claims.ClaimsPrincipal(identity);
            httpContext.User = principal;

            var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
            var service = new CurrentUserService(httpContextAccessor);

            // Act
            var externalId = service.ExternalId;

            // Assert
            Assert.Equal("some-oid-value", externalId);
        }

        [Fact]
        public void OrgId_ParsesFromHeader()
        {
            // Arrange
            var orgId = Guid.NewGuid();
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Headers.Add("X-Org-Id", orgId.ToString());

            var httpContextAccessor = new HttpContextAccessor { HttpContext = httpContext };
            var service = new CurrentUserService(httpContextAccessor);

            // Act
            var result = service.OrgId;

            // Assert
            Assert.Equal(orgId, result);
        }
    }
}
