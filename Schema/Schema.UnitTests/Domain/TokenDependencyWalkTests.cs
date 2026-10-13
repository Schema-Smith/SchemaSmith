// Copyright (c) SchemaSmith Contributors. Licensed under the SSCL v2.0.

using System;
using Schema.Domain;

namespace Schema.UnitTests.Domain
{
    [TestFixture]
    public class TokenDependencyWalkTests
    {
        [Test]
        public void TokenReferencingSchemaName_MarkedIterationScoped()
        {
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                QueryTokens = { ["TenantId"] = "<*Query*>SELECT TenantId FROM {{SchemaName}}.Config" }
            };

            template.ResolveTokenScopes();

            Assert.That(template.IsIterationScoped("TenantId"), Is.True);
        }

        [Test]
        public void TokenReferencingIterationScopedToken_TransitivelyIterationScoped()
        {
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                QueryTokens =
                {
                    ["TenantId"] = "<*Query*>SELECT TenantId FROM {{SchemaName}}.Config",
                    ["TenantLabel"] = "<*Query*>SELECT Label FROM Tenants WHERE Id = {{TenantId}}"
                }
            };

            template.ResolveTokenScopes();

            Assert.That(template.IsIterationScoped("TenantId"), Is.True);
            Assert.That(template.IsIterationScoped("TenantLabel"), Is.True);
        }

        [Test]
        public void TokenWithNoSchemaNameReference_StaysPerDb()
        {
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                QueryTokens = { ["Region"] = "<*Query*>SELECT Region FROM ServerProperties" }
            };

            template.ResolveTokenScopes();

            Assert.That(template.IsIterationScoped("Region"), Is.False);
        }

        [Test]
        public void TokenCycle_DetectedAtLoad()
        {
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                QueryTokens =
                {
                    ["A"] = "<*Query*>SELECT {{B}}",
                    ["B"] = "<*Query*>SELECT {{A}}"
                }
            };

            var ex = Assert.Throws<PackageException>(() => template.ResolveTokenScopes());
            Assert.That(ex!.Message, Does.Contain("cycle").IgnoreCase);
            // Message should name the tokens involved so the user can find them.
            Assert.That(ex.Message, Does.Contain("A"));
            Assert.That(ex.Message, Does.Contain("B"));
        }

        [Test]
        public void NonQueryToken_ReferencingSchemaName_AlsoEscalatedToIteration()
        {
            // Static (non-<*Query*>) tokens that splice {{SchemaName}} into their body need
            // per-iteration re-evaluation too — the substituted value differs per iteration.
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                NonQueryTokens = { ["TenantPrefix"] = "Prefix_{{SchemaName}}" }
            };

            template.ResolveTokenScopes();

            Assert.That(template.IsIterationScoped("TenantPrefix"), Is.True);
        }

        [Test]
        public void IsIterationScoped_BeforeResolveCalled_ReturnsFalse()
        {
            // Pre-resolve, the dispatcher / per-iteration code paths should never observe a "true".
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                QueryTokens = { ["X"] = "<*Query*>SELECT 1 FROM {{SchemaName}}.T" }
            };

            Assert.That(template.IsIterationScoped("X"), Is.False);
        }

        [Test]
        public void IsIterationScoped_UnknownToken_ReturnsFalse()
        {
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                QueryTokens = { ["TenantId"] = "<*Query*>SELECT 1 FROM {{SchemaName}}.T" }
            };
            template.ResolveTokenScopes();

            Assert.That(template.IsIterationScoped("Bogus"), Is.False);
        }

        [Test]
        public void IsIterationScoped_LookupIsCaseInsensitive()
        {
            // Token names are case-insensitive throughout the token system (SqlScript.TokenReplace
            // matches case-insensitively at runtime); the resolution map must agree, otherwise
            // callers querying with a casing different from the walk's keys would get wrong-false.
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                QueryTokens = { ["TenantId"] = "<*Query*>SELECT TenantId FROM {{SchemaName}}.Config" }
            };

            template.ResolveTokenScopes();

            Assert.That(template.IsIterationScoped("TenantId"), Is.True);
            Assert.That(template.IsIterationScoped("tenantid"), Is.True);
            Assert.That(template.IsIterationScoped("TENANTID"), Is.True);
        }

        [Test]
        public void TokenReferencingLowercaseSchemaName_AlsoMarkedIterationScoped()
        {
            // {{schemaname}} (any casing) is recognized as the iteration trigger — locks in the
            // case-insensitive matching the walk applies via TokenHelper.GetTokensFromString.
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                QueryTokens = { ["TenantId"] = "<*Query*>SELECT TenantId FROM {{schemaname}}.Config" }
            };

            template.ResolveTokenScopes();

            Assert.That(template.IsIterationScoped("TenantId"), Is.True);
        }

        [Test]
        public void TokenReferencingItself_DetectedAsCycle()
        {
            // Degenerate single-node cycle (A → A). The walk must surface this as a cycle rather
            // than silently looping or treating the self-reference as an unknown name.
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                QueryTokens = { ["A"] = "<*Query*>SELECT {{A}}" }
            };

            var ex = Assert.Throws<PackageException>(() => template.ResolveTokenScopes());
            Assert.That(ex!.Message, Does.Contain("cycle").IgnoreCase);
            Assert.That(ex.Message, Does.Contain("A"));
        }

        [Test]
        public void TokenCycle_ThreeNodes_DetectedAtLoad()
        {
            // A → B → C → A. All three should appear in the cycle-path message; the order in
            // which Walk happens to encounter them first doesn't matter, but each must be named.
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                QueryTokens =
                {
                    ["A"] = "<*Query*>SELECT {{B}}",
                    ["B"] = "<*Query*>SELECT {{C}}",
                    ["C"] = "<*Query*>SELECT {{A}}"
                }
            };

            var ex = Assert.Throws<PackageException>(() => template.ResolveTokenScopes());
            Assert.That(ex!.Message, Does.Contain("cycle").IgnoreCase);
            Assert.That(ex.Message, Does.Contain("A"));
            Assert.That(ex.Message, Does.Contain("B"));
            Assert.That(ex.Message, Does.Contain("C"));
        }

        // ── IsPerDb accessor (post-slice-8 cleanup, Commit B) ──

        [Test]
        public void IsPerDb_QueryTokenWithoutSchemaNameReference_ReturnsTrue()
        {
            // A query token with no {{SchemaName}} in its body (direct or transitive) is PerDb —
            // its resolved value depends on the target database but not on the iteration schema,
            // so the dispatcher can safely cache it across iterations within a single (server, DB).
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                QueryTokens = { ["Edition"] = "<*Query*>SELECT 'enterprise'" }
            };

            template.ResolveTokenScopes();

            Assert.That(template.IsPerDb("Edition"), Is.True);
            Assert.That(template.IsIterationScoped("Edition"), Is.False);
        }

        [Test]
        public void IsPerDb_QueryTokenThatReachesSchemaName_ReturnsFalse()
        {
            // An iteration-scoped query token must NOT be misclassified as PerDb — its resolved
            // value differs per iteration and caching it would leak one iteration's value into
            // siblings.
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                QueryTokens = { ["TenantId"] = "<*Query*>SELECT TenantId FROM {{SchemaName}}.Config" }
            };

            template.ResolveTokenScopes();

            Assert.That(template.IsPerDb("TenantId"), Is.False);
            Assert.That(template.IsIterationScoped("TenantId"), Is.True);
        }

        [Test]
        public void IsPerDb_NonQueryToken_ReturnsFalse()
        {
            // Static (PerProduct) tokens are not query tokens; the cache only applies to
            // <*Query*> tokens that incur a connection round-trip. NonQuery static tokens are
            // PerProduct and the accessor must NOT return true for them — that would let the
            // dispatcher try to populate a "cached value" for a token it should never pass to
            // ResolveQueryTokens in the first place.
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                NonQueryTokens = { ["StaticPrefix"] = "App_" }
            };

            template.ResolveTokenScopes();

            Assert.That(template.IsPerDb("StaticPrefix"), Is.False);
        }

        [Test]
        public void IsPerDb_BeforeResolveCalled_ReturnsFalse()
        {
            // Defensive default mirrors IsIterationScoped: pre-resolve the dispatcher must not
            // see a "true" answer for any token (the scope map hasn't been built yet, so we
            // don't know the answer — and "don't cache" is the safe default).
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                QueryTokens = { ["Edition"] = "<*Query*>SELECT 'enterprise'" }
            };

            Assert.That(template.IsPerDb("Edition"), Is.False);
        }

        [Test]
        public void IsPerDb_UnknownToken_ReturnsFalse()
        {
            // Mirrors IsIterationScoped's behavior on unknowns — the accessor must not assume
            // PerDb for a name it doesn't know.
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                QueryTokens = { ["Edition"] = "<*Query*>SELECT 'enterprise'" }
            };
            template.ResolveTokenScopes();

            Assert.That(template.IsPerDb("Bogus"), Is.False);
        }

        [Test]
        public void IsPerDb_NullToken_ReturnsFalse()
        {
            // Same null-tolerance contract as IsIterationScoped.
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                QueryTokens = { ["Edition"] = "<*Query*>SELECT 'enterprise'" }
            };
            template.ResolveTokenScopes();

            Assert.That(template.IsPerDb(null), Is.False);
        }

        [Test]
        public void IsPerDb_LookupIsCaseInsensitive()
        {
            // Matches IsIterationScoped's casing contract — keyed by case-insensitive comparer.
            var template = new Template
            {
                SchemaIdentificationScript = "SELECT 'tenant_a'",
                QueryTokens = { ["Edition"] = "<*Query*>SELECT 'enterprise'" }
            };

            template.ResolveTokenScopes();

            Assert.That(template.IsPerDb("Edition"), Is.True);
            Assert.That(template.IsPerDb("edition"), Is.True);
            Assert.That(template.IsPerDb("EDITION"), Is.True);
        }

        // ── GetOrCreatePerDbTokenCache (post-slice-8 cleanup, Commit B) ──

        [Test]
        public void GetOrCreatePerDbTokenCache_ReturnsSameInstanceForSameServerDbPair()
        {
            // Two calls with the same (server, database) must yield the same dictionary instance
            // so the dispatcher's deposits on call 1 are visible on call 2 (the whole point of
            // the cache).
            var template = new Template { Name = "T" };

            var first = template.GetOrCreatePerDbTokenCache("srv-a", "db-a");
            var second = template.GetOrCreatePerDbTokenCache("srv-a", "db-a");

            Assert.That(first, Is.Not.Null);
            Assert.That(second, Is.SameAs(first));
        }

        [Test]
        public void GetOrCreatePerDbTokenCache_DifferentDatabases_ReturnDifferentInstances()
        {
            // Each (server, database) must own a distinct cache — otherwise a per-DB token
            // resolved against DB-A would leak into DB-B's iterations.
            var template = new Template { Name = "T" };

            var cacheA = template.GetOrCreatePerDbTokenCache("srv-a", "db-a");
            var cacheB = template.GetOrCreatePerDbTokenCache("srv-a", "db-b");

            Assert.That(cacheA, Is.Not.SameAs(cacheB));
        }

        [Test]
        public void GetOrCreatePerDbTokenCache_DifferentServers_ReturnDifferentInstances()
        {
            // Same database name on different servers is two different targets.
            var template = new Template { Name = "T" };

            var cacheA = template.GetOrCreatePerDbTokenCache("srv-a", "db-a");
            var cacheB = template.GetOrCreatePerDbTokenCache("srv-b", "db-a");

            Assert.That(cacheA, Is.Not.SameAs(cacheB));
        }

        [Test]
        public void GetOrCreatePerDbTokenCache_NullOrEmptyServerOrDatabase_ReturnsNull()
        {
            // Defensive default — the dispatcher's fall-back is "no cache" rather than a
            // synthesized global cache that would defeat per-DB isolation.
            var template = new Template { Name = "T" };

            Assert.That(template.GetOrCreatePerDbTokenCache(null, "db"), Is.Null);
            Assert.That(template.GetOrCreatePerDbTokenCache("", "db"), Is.Null);
            Assert.That(template.GetOrCreatePerDbTokenCache("srv", null), Is.Null);
            Assert.That(template.GetOrCreatePerDbTokenCache("srv", ""), Is.Null);
        }

        [Test]
        public void GetOrCreatePerDbTokenCache_Clone_DoesNotInheritParentEntries()
        {
            // A clone represents a fresh resolution state — cached resolved values from the
            // original must not bleed into the clone, which may carry differently-evaluated
            // token bodies.
            var template = new Template
            {
                Name = "T",
                QueryTokens = { ["Edition"] = "<*Query*>SELECT 'enterprise'" }
            };
            template.Product = new Product { Name = "P", Platform = Platform.SqlServer };
            template.ResolveTokenScopes();

            var originalCache = template.GetOrCreatePerDbTokenCache("srv", "db");
            originalCache["Edition"] = "cached-value-from-original";

            var clone = template.Clone();
            var cloneCache = clone.GetOrCreatePerDbTokenCache("srv", "db");

            Assert.That(cloneCache, Is.Not.Null);
            Assert.That(cloneCache, Does.Not.ContainKey("Edition"));
        }

        [Test]
        public void GetOrCreatePerDbTokenCache_KeyIsCaseInsensitiveOnServerAndDatabase()
        {
            // SQL Server / PG database names are commonly case-insensitive in connection strings;
            // the dispatcher's (server, databaseName) inputs should not produce sibling cache
            // entries that differ only by casing.
            var template = new Template { Name = "T" };

            var lower = template.GetOrCreatePerDbTokenCache("SRV-A", "DB-A");
            var upper = template.GetOrCreatePerDbTokenCache("srv-a", "db-a");

            Assert.That(upper, Is.SameAs(lower));
        }
    }
}
