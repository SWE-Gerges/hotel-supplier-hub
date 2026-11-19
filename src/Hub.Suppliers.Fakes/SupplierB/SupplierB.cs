using Hub.Core;
using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Hub.Abstractions;
using Microsoft.Extensions.Options;

namespace Hub.Suppliers.Fakes;

// supplier-b speaks asynchronous token polling over snake_case JSON: StartSearch returns a token, then Poll(token)
// hands out result batches, out of order, until the status is "done". Prices are integer minor units and
// coordinates are [longitude, latitude] arrays.

internal sealed record BStartResponse(string? Token, int? Code, string? Message);

internal sealed record BPollResponse(string? Token, string Status, List<BBatch>? Batches, int? Code, string? Message);

internal sealed record BBatch(int BatchId, List<BHotel> Hotels);

internal sealed record BHotel(string HotelId, string Name, int Category, double[]? Coordinates, string? Street, List<BRoom>? Rooms, string? LocalName = null, string? LocalStreet = null, double[]? LocalCoordinates = null);

internal sealed record BRoom(string Id, string Title, int Meal, long PriceCents, string Currency, string? CancelUntil, string? PenaltyCode = null);

internal sealed record BStartRequest(string CityCode, Stay Stay);

internal static class SupplierBJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>The in-process stand-in for supplier-b's remote service.</summary>
internal sealed partial class SupplierBServer(SyntheticCatalog catalog, FaultController faults, TimeProvider time, IOptions<FakeSuppliersOptions> options, FakeSupplierLedger ledger)
{
    private static readonly TimeSpan SearchRetention = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, SearchState> _searches = new(StringComparer.Ordinal);

    public async Task<string> StartSearchAsync(BStartRequest request, CancellationToken cancellationToken)
    {
        var decision = await FakeSupplierSupport.ApplyFaultsAsync(faults, time, cancellationToken).ConfigureAwait(false);
        if (decision.Error is { } kind)
        {
            return Serialize(ErrorStart(kind), decision);
        }

        PurgeExpired();
        var city = catalog.Cities.FirstOrDefault(c => CityCodeOf(c) == request.CityCode);
        var hotels = city is null ? [] : catalog.HotelsListedBy(FakeSupplierIds.SupplierB, city.CanonicalId);

        var token = Guid.NewGuid().ToString("N");
        var now = time.GetUtcNow();
        var settings = options.Value.SupplierB;
        var batches = BuildBatches(hotels, request.Stay, token, settings, now);
        _searches[token] = new SearchState(batches, now);
        return Serialize(new BStartResponse(token, null, null), decision);
    }

    public async Task<string> PollAsync(string token, CancellationToken cancellationToken)
    {
        var decision = await FakeSupplierSupport.ApplyFaultsAsync(faults, time, cancellationToken).ConfigureAwait(false);
        if (decision.Error is { } kind)
        {
            return Serialize(ErrorPoll(kind), decision);
        }

        if (!_searches.TryGetValue(token, out var state))
        {
            return Serialize(new BPollResponse(token, "error", null, 404, "Unknown or expired search token."), decision);
        }

        var now = time.GetUtcNow();
        var ready = new List<BBatch>();
        bool done;
        lock (state)
        {
            foreach (var pending in state.Pending.Where(p => p.ReadyAt <= now).ToList())
            {
                ready.Add(pending.Batch);
                state.Pending.Remove(pending);
            }

            done = state.Pending.Count == 0;
        }

        if (done)
        {
            _searches.TryRemove(token, out _);
        }

        return Serialize(new BPollResponse(token, done ? "done" : ready.Count > 0 ? "partial" : "pending", ready, null, null), decision);
    }

    public static string CityCodeOf(FakeCity city) => city.CanonicalId switch
    {
        "city-aldermoor" => "5001",
        "city-brightwater" => "5002",
        _ => "5003",
    };

    private List<PendingBatch> BuildBatches(
        IReadOnlyList<PhysicalHotel> hotels, Stay stay, string token, SupplierBOptions settings, DateTimeOffset now)
    {
        var hotelDtos = new List<BHotel>();
        for (var index = 0; index < hotels.Count; index++)
        {
            var hotel = hotels[index];
            var offers = catalog.OffersFor(FakeSupplierIds.SupplierB, hotel, stay);
            if (offers.Count == 0)
            {
                continue;
            }

            hotelDtos.Add(new BHotel(
                catalog.CodeFor(FakeSupplierIds.SupplierB, hotel),
                catalog.NameFor(FakeSupplierIds.SupplierB, hotel),
                hotel.Stars,
                [hotel.Longitude, hotel.Latitude],
                hotel.Street,
                offers.Select(o => ToRoom(o, stay)).ToList(),
                LocalNameFor(settings.Overlay, hotel, hotels[(index + 1) % hotels.Count]),
                LocalStreetFor(settings.Overlay, hotel, hotels[(index + 1) % hotels.Count]),
                LocalCoordinatesFor(settings.Overlay, hotel, hotels[(index + 1) % hotels.Count])));
        }

        var chunks = hotelDtos.Chunk(settings.BatchSize).Select((chunk, i) => new BBatch(i + 1, [.. chunk])).ToList();

        // Batches become available in a token-dependent order, so they arrive out of sequence.
        var tokenKey = DeterministicHash.OfString(token);
        var order = chunks.OrderBy(b => DeterministicHash.Mix(tokenKey, (ulong)b.BatchId)).ToList();
        return order.Select((batch, slot) => new PendingBatch(batch, now + (settings.BatchDelay * slot))).ToList();
    }

    private static string? LocalNameFor(FakeOverlayMode mode, PhysicalHotel hotel, PhysicalHotel other) => mode switch
    {
        FakeOverlayMode.Correct => $"Hôtel {hotel.Brand} {hotel.Qualifier}",
        FakeOverlayMode.Wrong => $"Hôtel {other.Brand} {other.Qualifier}",
        _ => null,
    };

    private static string? LocalStreetFor(FakeOverlayMode mode, PhysicalHotel hotel, PhysicalHotel other) => mode switch
    {
        FakeOverlayMode.Correct => $"{hotel.Street} (local)",
        FakeOverlayMode.Wrong => $"{other.Street} (local)",
        _ => null,
    };

    private static double[]? LocalCoordinatesFor(FakeOverlayMode mode, PhysicalHotel hotel, PhysicalHotel other) => mode switch
    {
        FakeOverlayMode.Correct => [Math.Round(hotel.Longitude + 0.0002, 5), Math.Round(hotel.Latitude + 0.0001, 5)],
        FakeOverlayMode.Wrong => [other.Longitude, other.Latitude],
        _ => null,
    };

    private static BRoom ToRoom(PhysicalOffer offer, Stay stay)
    {
        var grade = offer.Room.Grade.ToUpperInvariant();
        var bed = offer.Room.Bed switch { "Double" => "DBL", "Twin" => "TWN", _ => "STE" };
        var view = offer.Room.View is null ? string.Empty : " " + offer.Room.View.Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant();

        return new BRoom(
            offer.Room.Code,
            $"{grade} {bed}{view}",
            MealCode(offer.Board),
            (long)Math.Round(offer.Total * 100m, 0, MidpointRounding.AwayFromZero),
            stay.Currency,
            offer.Refundable ? $"{offer.FreeCancellationUntil.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}T00:00:00Z" : null,
            offer.Refundable ? "NIGHT1" : "FULL");
    }

    private static int MealCode(BoardType board) => board switch
    {
        BoardType.RoomOnly => 1,
        BoardType.Breakfast => 2,
        BoardType.HalfBoard => 3,
        BoardType.FullBoard => 4,
        _ => 5,
    };

    private void PurgeExpired()
    {
        var cutoff = time.GetUtcNow() - SearchRetention;
        foreach (var (token, state) in _searches)
        {
            if (state.StartedAt < cutoff)
            {
                _searches.TryRemove(token, out _);
            }
        }
    }

    private static BStartResponse ErrorStart(SupplierErrorKind kind) => new(null, StatusFor(kind), "The supplier reported an error.");

    private static BPollResponse ErrorPoll(SupplierErrorKind kind) => new(null, "error", null, StatusFor(kind), "The supplier reported an error.");

    private static int StatusFor(SupplierErrorKind kind) => kind switch
    {
        SupplierErrorKind.Transient => 503,
        SupplierErrorKind.RateLimited => 429,
        SupplierErrorKind.Unavailable => 410,
        SupplierErrorKind.Auth => 401,
        SupplierErrorKind.InvalidRequest => 400,
        SupplierErrorKind.Timeout => 504,
        _ => 500,
    };

    private static string Serialize<T>(T payload, FaultDecision decision)
    {
        var json = JsonSerializer.Serialize(payload, SupplierBJson.Options);
        return decision.Malformed ? FakeSupplierSupport.Corrupt(json) : json;
    }

    private sealed record PendingBatch(BBatch Batch, DateTimeOffset ReadyAt);

    private sealed class SearchState(List<PendingBatch> pending, DateTimeOffset startedAt)
    {
        public List<PendingBatch> Pending { get; } = pending;

        public DateTimeOffset StartedAt { get; } = startedAt;
    }
}

internal sealed class SupplierBNormalizer(RoomExtraMapperRegistry extras) : ISupplierResponseNormalizer<BBatch>
{
    public SupplierBNormalizer()
        : this(RoomExtraMapperRegistry.Of(new SupplierBRoomExtraMapper()))
    {
    }

    public Result<IReadOnlyList<Hotel>> Normalize(BBatch response, SupplierSearchQuery query)
    {
        var hotels = new List<Hotel>();
        foreach (var raw in response.Hotels ?? [])
        {
            var offers = (raw.Rooms ?? []).Select(room => ToOffer(raw.HotelId, room, query)).OfType<RoomOffer>().ToList();
            if (offers.Count == 0)
            {
                continue;
            }

            hotels.Add(new Hotel
            {
                Name = raw.Name.Trim(),
                Stars = Math.Clamp(raw.Category, 0, 5),
                Location = raw.Coordinates is { Length: 2 } c ? new GeoPoint(Latitude: c[1], Longitude: c[0]) : null,
                Address = raw.Street ?? string.Empty,
                Offers = offers,
                Sources = [new SupplierHotelRef(FakeSupplierIds.SupplierB, raw.HotelId)],
                Overlay = raw.LocalName is { Length: > 0 } localName
                    ? new LocalizedOverlay("xa", localName.Trim(), raw.LocalStreet, raw.LocalCoordinates is { Length: 2 } l ? new GeoPoint(Latitude: l[1], Longitude: l[0]) : null)
                    : null,
            });
        }

        return Result.Ok<IReadOnlyList<Hotel>>(hotels);
    }

    public static SupplierError MapError(int? code, string? message)
    {
        var text = message ?? "The supplier reported an error.";
        return code switch
        {
            503 => SupplierError.Transient(text),
            429 => new SupplierError(SupplierErrorKind.RateLimited, text),
            410 => SupplierError.Unavailable(text),
            409 => SupplierError.PriceChanged(text),
            422 => SupplierError.SoldOut(text),
            404 => SupplierError.NotFound(text),
            401 => new SupplierError(SupplierErrorKind.Auth, text),
            400 => SupplierError.InvalidRequest(text),
            504 => SupplierError.Timeout(text),
            _ => SupplierError.Unknown(text, code?.ToString(CultureInfo.InvariantCulture)),
        };
    }

    public static BoardType ParseBoard(int meal) => meal switch
    {
        1 => BoardType.RoomOnly,
        2 => BoardType.Breakfast,
        3 => BoardType.HalfBoard,
        4 => BoardType.FullBoard,
        5 => BoardType.AllInclusive,
        _ => BoardType.Unknown,
    };

    private RoomOffer? ToOffer(string hotelCode, BRoom room, SupplierSearchQuery query)
    {
        if (room.PriceCents < 0 || room.Currency is not { Length: 3 })
        {
            return null;
        }

        var price = new Money(room.PriceCents / 100m, room.Currency.ToUpperInvariant());
        var stay = query.Request.Stay;
        return new RoomOffer
        {
            OfferId = $"{FakeSupplierIds.SupplierBValue}.{hotelCode}.{room.Id}.{room.Meal}.{stay.CheckIn:yyyyMMdd}.{stay.Nights}",
            Supplier = FakeSupplierIds.SupplierB,
            SupplierHotelCode = hotelCode,
            RoomName = room.Title.Trim(),
            Board = ParseBoard(room.Meal),
            RawBoard = room.Meal.ToString(CultureInfo.InvariantCulture),
            Refundable = false, // the hub's policy rules decide; the raw date and penalty code travel in Extra
            Price = price,
            SellingPrice = price,
            Extra = extras.Map(room, new RoomExtraContext(FakeSupplierIds.SupplierB, hotelCode)),
        };
    }
}

/// <summary>supplier-b client: a producer polls the supplier, a consumer normalises batches as they arrive.</summary>
internal sealed partial class SupplierBClient(
    SupplierBServer server,
    IOptions<FakeSuppliersOptions> options,
    SupplierBNormalizer normalizer,
    TimeProvider time) : ISupplierDescriptor, IHotelSearch, IRoomAvailability, IBook, ICancel, IRetrieve
{
    private const int ChannelCapacity = 4;

    public SupplierId SupplierId => FakeSupplierIds.SupplierB;

    public SupplierCapabilities Capabilities =>
        SupplierCapabilities.Search | SupplierCapabilities.RoomAvailability | SupplierCapabilities.Book
        | SupplierCapabilities.Cancel | SupplierCapabilities.Retrieve | SupplierCapabilities.DuplicateDetection | SupplierCapabilities.CityDirectory;

    public SearchStyle SearchStyle => SearchStyle.AsyncPolling;

    public TimeSpan DefaultTimeout => TimeSpan.FromSeconds(8);

    public int MaxConcurrency => 4;

    public async IAsyncEnumerable<Result<IReadOnlyList<Hotel>>> SearchAsync(
        SupplierSearchQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (FakeSupplierSupport.ValidateQuery(query) is { } invalid)
        {
            yield return Result.Fail<IReadOnlyList<Hotel>>(invalid);
            yield break;
        }

        var channel = Channel.CreateBounded<PollItem>(new BoundedChannelOptions(ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
        });

        using var producerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = ProduceAsync(query, channel.Writer, producerCancellation.Token);
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var result = item.Failure is { } failure
                    ? Result.Fail<IReadOnlyList<Hotel>>(failure)
                    : normalizer.Normalize(item.Batch!, query);

                yield return result;
                if (result.IsFailure)
                {
                    yield break;
                }
            }
        }
        finally
        {
            // Stop the poller first, then wait for it, so nothing outlives the enumeration.
            await producerCancellation.CancelAsync().ConfigureAwait(false);
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: the producer was stopped on purpose.
            }
        }
    }

    private async Task ProduceAsync(SupplierSearchQuery query, ChannelWriter<PollItem> writer, CancellationToken cancellationToken)
    {
        var settings = options.Value.SupplierB;
        try
        {
            var cityCode = query.Codes.CityCode;
            var started = await FakeSupplierSupport.GuardAsync(
                async () => await StartAsync(cityCode, query, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            if (started.IsFailure)
            {
                await writer.WriteAsync(PollItem.Fail(started.Error), cancellationToken).ConfigureAwait(false);
                return;
            }

            var token = started.Value;
            var startedAt = time.GetTimestamp();
            while (true)
            {
                if (time.GetElapsedTime(startedAt) >= settings.MaxWait)
                {
                    await writer.WriteAsync(
                        PollItem.Fail(SupplierError.Timeout($"The supplier did not finish within {settings.MaxWait.TotalSeconds:0.##}s of polling.")),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                var poll = await FakeSupplierSupport.GuardAsync(
                    async () => await PollOnceAsync(token, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                if (poll.IsFailure)
                {
                    await writer.WriteAsync(PollItem.Fail(poll.Error), cancellationToken).ConfigureAwait(false);
                    return;
                }

                foreach (var batch in poll.Value.Batches ?? [])
                {
                    await writer.WriteAsync(PollItem.Of(batch), cancellationToken).ConfigureAwait(false);
                }

                if (poll.Value.Status == "done")
                {
                    return;
                }

                await Task.Delay(settings.PollInterval, time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async ValueTask<Result<string>> StartAsync(string cityCode, SupplierSearchQuery query, CancellationToken cancellationToken)
    {
        var json = await server.StartSearchAsync(new BStartRequest(cityCode, FakeSupplierSupport.ToStay(query.Request)), cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<BStartResponse>(json, SupplierBJson.Options) ?? throw new JsonException("Empty response.");
        return response.Token is { Length: > 0 } token
            ? Result.Ok(token)
            : Result.Fail<string>(SupplierBNormalizer.MapError(response.Code, response.Message));
    }

    private async ValueTask<Result<BPollResponse>> PollOnceAsync(string token, CancellationToken cancellationToken)
    {
        var json = await server.PollAsync(token, cancellationToken).ConfigureAwait(false);
        var response = JsonSerializer.Deserialize<BPollResponse>(json, SupplierBJson.Options) ?? throw new JsonException("Empty response.");
        return response.Status == "error"
            ? Result.Fail<BPollResponse>(SupplierBNormalizer.MapError(response.Code, response.Message))
            : Result.Ok(response);
    }

    private sealed record PollItem(BBatch? Batch, SupplierError? Failure)
    {
        public static PollItem Of(BBatch batch) => new(batch, null);

        public static PollItem Fail(SupplierError error) => new(null, error);
    }
}
