// SPDX-License-Identifier: MIT
// SPDX-FileCopyrightText: 2026 Harald Lapp

using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

using Papuma.Kernel.Validation;

namespace Papuma.Kernel.Model;

/// <summary>
/// Builds the kernel metamodel at startup (architecture §6). Policy and key defaults
/// come from attributes on the document classes; fluent configuration overrides them
/// per deployment (ADR-007).
/// </summary>
public sealed class KernelModelBuilder
{
    private readonly Dictionary<Type, IDocumentTypeBuilder> _builders = [];
    private readonly Dictionary<Type, IEventTypeBuilder> _eventBuilders = [];

    /// <summary>
    /// Registers a document type using attribute defaults only.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    public KernelModelBuilder Document<T>() where T : class => Document<T>(_ => { });

    /// <summary>
    /// Registers a document type with fluent overrides on top of the attribute defaults.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    /// <param name="configure">The fluent configuration.</param>
    public KernelModelBuilder Document<T>(Action<DocumentTypeBuilder<T>> configure) where T : class
    {
        ArgumentNullException.ThrowIfNull(configure);

        if (_builders.ContainsKey(typeof(T)))
        {
            throw new InvalidOperationException($"Document type {typeof(T).Name} is already registered.");
        }

        var builder = new DocumentTypeBuilder<T>();
        configure(builder);
        _builders[typeof(T)] = builder;
        return this;
    }

    /// <summary>
    /// Registers an event type (ADR-013) using attribute defaults only.
    /// </summary>
    /// <typeparam name="T">The event CLR type.</typeparam>
    public KernelModelBuilder Event<T>() where T : class => Event<T>(_ => { });

    /// <summary>
    /// Registers an event type (ADR-013) with fluent overrides on top of the
    /// attribute defaults.
    /// </summary>
    /// <typeparam name="T">The event CLR type.</typeparam>
    /// <param name="configure">The fluent configuration.</param>
    public KernelModelBuilder Event<T>(Action<EventTypeBuilder<T>> configure) where T : class
    {
        ArgumentNullException.ThrowIfNull(configure);

        if (_eventBuilders.ContainsKey(typeof(T)))
        {
            throw new InvalidOperationException($"Event type {typeof(T).Name} is already registered.");
        }

        var builder = new EventTypeBuilder<T>();
        configure(builder);
        _eventBuilders[typeof(T)] = builder;
        return this;
    }

    /// <summary>
    /// Builds the immutable model. Throws when a registered type has no resolvable id.
    /// </summary>
    public KernelModel Build()
    {
        var byType = new Dictionary<Type, DocumentTypeMetadata>();
        foreach (var (clrType, builder) in _builders)
        {
            byType[clrType] = builder.Build();
        }

        var eventsByType = new Dictionary<Type, EventTypeMetadata>();
        foreach (var (clrType, builder) in _eventBuilders)
        {
            eventsByType[clrType] = builder.Build();
        }

        return new KernelModel(byType, eventsByType);
    }

    private interface IDocumentTypeBuilder
    {
        DocumentTypeMetadata Build();
    }

    private interface IEventTypeBuilder
    {
        EventTypeMetadata Build();
    }

    /// <summary>
    /// Fluent configuration of one event type (ADR-013). Events are immutable facts —
    /// there are deliberately no upcasters and no keys; transforming changes require a
    /// new event type.
    /// </summary>
    /// <typeparam name="T">The event CLR type.</typeparam>
    public sealed class EventTypeBuilder<T> : IEventTypeBuilder where T : class
    {
        private readonly Dictionary<string, FieldPolicy> _policyOverrides = new(StringComparer.Ordinal);
        private TimeSpan? _retention;

        internal EventTypeBuilder()
        {
        }

        /// <summary>
        /// Starts a policy override for a payload property (overrides any attribute default).
        /// </summary>
        public EventPropertyPolicyBuilder Property(Expression<Func<T, object?>> property)
        {
            ArgumentNullException.ThrowIfNull(property);
            return new EventPropertyPolicyBuilder(this, JsonPathResolver.Resolve(property));
        }

        /// <summary>
        /// Declares a retention period: events older than this may be purged by
        /// <c>EventRetention</c> (opt-in, ADR-013).
        /// </summary>
        public EventTypeBuilder<T> Retention(TimeSpan retention)
        {
            if (retention < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(retention), "Retention must not be negative.");
            }

            _retention = retention;
            return this;
        }

        EventTypeMetadata IEventTypeBuilder.Build()
        {
            var clrType = typeof(T);
            var name = clrType.Name;
            InputValidator.ValidateDocumentType(name);

            var policies = new Dictionary<string, FieldPolicy>(StringComparer.Ordinal);
            var ignoredKeys = new Dictionary<string, bool>(StringComparer.Ordinal);
            ScanType(clrType, prefix: string.Empty, policies, ignoredKeys, visited: []);

            foreach (var (path, policy) in _policyOverrides)
            {
                if (policy == FieldPolicy.Track)
                {
                    policies.Remove(path);
                }
                else
                {
                    policies[path] = policy;
                }
            }

            // Reference has no source location for events — the event IS the record (ADR-013).
            var reference = policies.FirstOrDefault(p => p.Value == FieldPolicy.Reference);
            if (reference.Key is not null)
            {
                throw new InvalidOperationException(
                    $"Event type {name}: property '{reference.Key}' uses the Reference policy, " +
                    "which is not applicable to events — there is no document the reference could " +
                    "point to. Use Redact or Hash instead (ADR-013).");
            }

            return new EventTypeMetadata(name, clrType, policies, _retention);
        }

        internal void SetPolicy(string path, FieldPolicy policy) => _policyOverrides[path] = policy;

        /// <summary>
        /// Fluent policy selection for one event payload property.
        /// </summary>
        public sealed class EventPropertyPolicyBuilder
        {
            private readonly EventTypeBuilder<T> _parent;
            private readonly string _path;

            internal EventPropertyPolicyBuilder(EventTypeBuilder<T> parent, string path)
            {
                _parent = parent;
                _path = path;
            }

            /// <summary>Stores the field verbatim (resets an attribute default).</summary>
            public EventTypeBuilder<T> Track() => Set(FieldPolicy.Track);

            /// <summary>Removes the field from the stored payload.</summary>
            public EventTypeBuilder<T> Redact() => Set(FieldPolicy.Redact);

            /// <summary>Replaces the field value with a SHA-256 hex hash.</summary>
            public EventTypeBuilder<T> StoreAsHash() => Set(FieldPolicy.Hash);

            /// <summary>Removes the field from the stored payload.</summary>
            public EventTypeBuilder<T> DoNotTrack() => Set(FieldPolicy.DoNotTrack);

            private EventTypeBuilder<T> Set(FieldPolicy policy)
            {
                _parent.SetPolicy(_path, policy);
                return _parent;
            }
        }
    }

    /// <summary>
    /// Fluent configuration of one document type.
    /// </summary>
    /// <typeparam name="T">The document CLR type.</typeparam>
    public sealed class DocumentTypeBuilder<T> : IDocumentTypeBuilder where T : class
    {
        private readonly Dictionary<string, FieldPolicy> _policyOverrides = new(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _keyOverrides = new(StringComparer.Ordinal); // path → unique
        private readonly Dictionary<int, Action<System.Text.Json.Nodes.JsonObject>> _upcasters = [];
        private LambdaExpression? _idExpression;
        private Action<object>? _validator;
        private bool _exposeToMcp;

        internal DocumentTypeBuilder()
        {
        }

        /// <summary>
        /// Allows this document type to be read over the MCP content tools as a
        /// policy-projected (masked) document (ADR-016). Off by default — the safe
        /// default is that no type is readable by an AI agent. Masking applies the
        /// field policies on read, so sensitive fields never reach the agent in clear
        /// text; this is data minimization, not authorization (that stays scope +
        /// the MCP host's auth).
        /// </summary>
        public DocumentTypeBuilder<T> ExposeToMcp()
        {
            _exposeToMcp = true;
            return this;
        }

        /// <summary>
        /// Declares the id property explicitly. Without this call, a string property
        /// named <c>Id</c> is required by convention.
        /// </summary>
        public DocumentTypeBuilder<T> HasId(Expression<Func<T, string>> id)
        {
            ArgumentNullException.ThrowIfNull(id);
            _idExpression = id;
            return this;
        }

        /// <summary>
        /// Starts a policy override for a property (overrides any attribute default).
        /// </summary>
        public PropertyPolicyBuilder Property(Expression<Func<T, object?>> property)
        {
            ArgumentNullException.ThrowIfNull(property);
            return new PropertyPolicyBuilder(this, JsonPathResolver.Resolve(property));
        }

        /// <summary>
        /// Declares a unique key (ADR-006) on a property (<c>x => x.Email</c>) or, composite,
        /// on several properties together (<c>x => new { x.ProjectId, x.Number }</c>, ADR-020).
        /// A composite key is enforced only for documents that carry every component.
        /// </summary>
        /// <param name="property">The key property, or an anonymous type of key properties.</param>
        public DocumentTypeBuilder<T> UniqueKey(Expression<Func<T, object?>> property)
        {
            ArgumentNullException.ThrowIfNull(property);
            _keyOverrides[JsonPathResolver.ResolveKey(property)] = true;
            return this;
        }

        /// <summary>
        /// Declares a non-unique lookup key (ADR-006) on a property or, composite, on
        /// several properties together (<c>x => new { x.ProjectId, x.Status }</c>, ADR-020).
        /// </summary>
        /// <param name="property">The key property, or an anonymous type of key properties.</param>
        public DocumentTypeBuilder<T> LookupKey(Expression<Func<T, object?>> property)
        {
            ArgumentNullException.ThrowIfNull(property);
            _keyOverrides[JsonPathResolver.ResolveKey(property)] = false;
            return this;
        }

        /// <summary>
        /// Registers a validator that is run against the stored result of every patch
        /// before commit — throw any exception to reject and roll back (ADR-012, point 5).
        /// </summary>
        /// <param name="validator">The validator; throws to reject the patched state.</param>
        public DocumentTypeBuilder<T> Validate(Action<T> validator)
        {
            ArgumentNullException.ThrowIfNull(validator);
            _validator = document => validator((T)document);
            return this;
        }

        /// <summary>
        /// Registers an upcaster that lifts raw documents from
        /// <paramref name="fromVersion"/> to <paramref name="fromVersion"/> + 1 (ADR-005).
        /// The current schema version of the type becomes the highest <c>fromVersion</c> + 1.
        /// </summary>
        /// <remarks>
        /// Only transforming changes need an upcaster — renames, restructurings, type
        /// changes, derived defaults. Additive changes (new optional property with a
        /// constant default, removed property, new enum value) are covered by JSON
        /// deserialization and require neither an upcaster nor a version bump.
        /// Never simulate a transformation additively (ADR-005, point 7). Upcasters must
        /// not be removed while documents of their source version may still exist.
        /// </remarks>
        /// <param name="fromVersion">The schema version this upcaster reads (≥ 1).</param>
        /// <param name="upcast">Mutates the raw JSON document in place to the next version.</param>
        public DocumentTypeBuilder<T> Upcast(int fromVersion, Action<System.Text.Json.Nodes.JsonObject> upcast)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(fromVersion, 1);
            ArgumentNullException.ThrowIfNull(upcast);

            if (!_upcasters.TryAdd(fromVersion, upcast))
            {
                throw new InvalidOperationException(
                    $"An upcaster from version {fromVersion} is already registered for {typeof(T).Name}.");
            }

            return this;
        }

        DocumentTypeMetadata IDocumentTypeBuilder.Build()
        {
            var clrType = typeof(T);
            var name = clrType.Name;
            InputValidator.ValidateDocumentType(name);

            // 1. Attribute defaults, scanned recursively into nested POCO types.
            var policies = new Dictionary<string, FieldPolicy>(StringComparer.Ordinal);
            var keys = new Dictionary<string, bool>(StringComparer.Ordinal);
            ScanType(clrType, prefix: string.Empty, policies, keys, visited: []);

            // 2. Fluent overrides win.
            foreach (var (path, policy) in _policyOverrides)
            {
                if (policy == FieldPolicy.Track)
                {
                    policies.Remove(path); // explicit Track() resets an attribute default
                }
                else
                {
                    policies[path] = policy;
                }
            }

            foreach (var (path, unique) in _keyOverrides)
            {
                keys[path] = unique;
            }

            var keyMetadata = keys
                .OrderBy(k => k.Key, StringComparer.Ordinal)
                .Select(k => new KeyMetadata(k.Key, k.Value, BuildIndexName(name, k.Key, k.Value)))
                .ToList();

            // The upcaster chain must be contiguous from 1: lazy upcasting may encounter
            // documents of any historical version (ADR-005).
            var orderedUpcasters = new List<Action<System.Text.Json.Nodes.JsonObject>>(_upcasters.Count);
            for (var version = 1; version <= _upcasters.Count; version++)
            {
                if (!_upcasters.TryGetValue(version, out var upcaster))
                {
                    throw new InvalidOperationException(
                        $"Document type {name}: upcaster chain has a gap — no upcaster from version {version}, " +
                        $"but versions up to {_upcasters.Keys.Max()} are registered. " +
                        "Upcasters must form a contiguous chain starting at 1.");
                }

                orderedUpcasters.Add(upcaster);
            }

            return new DocumentTypeMetadata(
                name, clrType, policies, keyMetadata, BuildIdGetter(clrType), orderedUpcasters, _validator,
                _exposeToMcp);
        }

        internal void SetPolicy(string path, FieldPolicy policy) => _policyOverrides[path] = policy;

        private Func<object, string?> BuildIdGetter(Type clrType)
        {
            PropertyInfo idProperty;
            if (_idExpression is not null)
            {
                var body = _idExpression.Body is UnaryExpression { NodeType: ExpressionType.Convert } u
                    ? u.Operand
                    : _idExpression.Body;
                idProperty = (PropertyInfo)((MemberExpression)body).Member;
            }
            else
            {
                idProperty = clrType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)
                    ?? throw new InvalidOperationException(
                        $"Document type {clrType.Name} has no public 'Id' property. " +
                        "Add one or declare the id via HasId(...).");

                if (idProperty.PropertyType != typeof(string))
                {
                    throw new InvalidOperationException(
                        $"Document type {clrType.Name}: the conventional 'Id' property must be a string.");
                }
            }

            return document => (string?)idProperty.GetValue(document);
        }

        /// <summary>
        /// Fluent policy selection for one property.
        /// </summary>
        public sealed class PropertyPolicyBuilder
        {
            private readonly DocumentTypeBuilder<T> _parent;
            private readonly string _path;

            internal PropertyPolicyBuilder(DocumentTypeBuilder<T> parent, string path)
            {
                _parent = parent;
                _path = path;
            }

            /// <summary>Records old and new value verbatim (resets an attribute default).</summary>
            public DocumentTypeBuilder<T> Track() => Set(FieldPolicy.Track);

            /// <summary>Records only that the field changed.</summary>
            public DocumentTypeBuilder<T> Redact() => Set(FieldPolicy.Redact);

            /// <summary>Records a reference to the value's location instead of the value.</summary>
            public DocumentTypeBuilder<T> StoreAsReference() => Set(FieldPolicy.Reference);

            /// <summary>Records a change marker plus a SHA-256 hash of the new value.</summary>
            public DocumentTypeBuilder<T> StoreAsHash() => Set(FieldPolicy.Hash);

            /// <summary>Excludes the field from diffs entirely.</summary>
            public DocumentTypeBuilder<T> DoNotTrack() => Set(FieldPolicy.DoNotTrack);

            private DocumentTypeBuilder<T> Set(FieldPolicy policy)
            {
                _parent.SetPolicy(_path, policy);
                return _parent;
            }
        }
    }

    private static void ScanType(
        Type type,
        string prefix,
        Dictionary<string, FieldPolicy> policies,
        Dictionary<string, bool> keys,
        HashSet<Type> visited)
    {
        if (!visited.Add(type))
        {
            return; // cycle guard
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (JsonPathResolver.IsIgnored(property))
            {
                continue; // not serialized → no JSON path → no policy/key (review L7)
            }

            var jsonName = JsonPathResolver.JsonNameOf(property);
            var path = prefix.Length == 0 ? jsonName : $"{prefix}.{jsonName}";

            var policy = PolicyFromAttributes(property);
            if (policy is not null)
            {
                policies[path] = policy.Value;
            }

            if (property.GetCustomAttribute<UniqueKeyAttribute>() is not null)
            {
                keys[path] = true;
            }
            else if (property.GetCustomAttribute<LookupKeyAttribute>() is not null)
            {
                keys[path] = false;
            }

            if (IsNestedPocoType(property.PropertyType))
            {
                ScanType(property.PropertyType, path, policies, keys, visited);
            }
        }

        visited.Remove(type);
    }

    private static FieldPolicy? PolicyFromAttributes(PropertyInfo property)
    {
        if (property.GetCustomAttribute<DoNotTrackAttribute>() is not null)
        {
            return FieldPolicy.DoNotTrack;
        }

        if (property.GetCustomAttribute<TrackHashAttribute>() is not null)
        {
            return FieldPolicy.Hash;
        }

        if (property.GetCustomAttribute<TrackReferenceAttribute>() is not null)
        {
            return FieldPolicy.Reference;
        }

        if (property.GetCustomAttribute<SensitiveDataAttribute>() is not null)
        {
            return FieldPolicy.Redact;
        }

        return null;
    }

    private static bool IsNestedPocoType(Type type)
    {
        if (!type.IsClass || type == typeof(string))
        {
            return false;
        }

        // Collections and dictionaries are atomic diff values (ADR-004) — policies on
        // their element types cannot be applied per element and are not scanned.
        return !typeof(IEnumerable).IsAssignableFrom(type);
    }

    /// <summary>
    /// Builds a deterministic index name (≤ 63 chars, PostgreSQL's NAMEDATALEN limit —
    /// harmless truncation-and-hash headroom for other storage backends too) for a declared key.
    /// </summary>
    internal static string BuildIndexName(string documentType, string path, bool unique)
    {
        var prefix = unique ? "ux" : "ix";
        // Composite components are joined by "__" so (a, b) cannot collide with the path a.b.
        var pathPart = path.Replace(KeyMetadata.ComponentSeparator.ToString(), "__", StringComparison.Ordinal).Replace('.', '_');
        var raw = $"{prefix}_papuma_doc_{documentType}_{pathPart}".ToLowerInvariant();
        if (raw.Length <= 63)
        {
            return raw;
        }

        // PostgreSQL truncates identifiers at 63 bytes; other backends aren't bound by that
        // limit but the same truncate-and-hash scheme is still a valid, stable name for them.
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)))[..8];
        return $"{raw[..54]}_{hash}";
    }
}
