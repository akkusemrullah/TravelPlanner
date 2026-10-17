using System.Globalization;
using System.Net;
using System.Text.Json;
using TravelPlanner.Helpers;
using TravelPlanner.Resources.Strings;
using TravelPlanner.ViewModels;

namespace TravelPlanner.Views;

public partial class MapPage : ContentPage
{
    // TP-108 + Sprint 4 free-API audit: the map library is shipped inside the app (Resources/Raw/maplibre)
    // instead of being pulled from a CDN on every render. The files are read once and inlined into the HTML,
    // which works the same on Android, iOS and Windows without depending on each platform's WebView base URL.
    private static string? _mapLibreJs;
    private static string? _mapLibreCss;

    // Map data: OpenFreeMap (https://openfreemap.org) - free, no API key, no registration, no request limits
    // and commercial use allowed (OpenStreetMap data via OpenMapTiles). It replaces CARTO's raster basemap,
    // which now requires an API key for raster tiles and limits free commercial use. OpenFreeMap serves
    // vector tiles, hence MapLibre GL JS (BSD-3) instead of Leaflet. The style JSON carries the required
    // attribution, which MapLibre's attribution control renders automatically.
    private const string LightStyleUrl = "https://tiles.openfreemap.org/styles/liberty";
    private const string DarkStyleUrl = "https://tiles.openfreemap.org/styles/dark";

    private readonly MapViewModel _viewModel;

    /// <summary>The map page is in the WebView (built once per page; only a theme change builds it again).</summary>
    private bool _rendered;
    private bool _renderedDark;

    /// <summary>The WebView finished loading the page, so applyData can be called.</summary>
    private bool _ready;
    private bool _pendingUpdate;
    private bool _pendingFit;

    public MapPage(MapViewModel viewModel)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;

        // another route day: new pins numbering and line, zoomed to that day
        _viewModel.RouteChanged += (_, _) =>
        {
            if (_rendered)
                UpdateMapAsync(fit: true).FireAndForget();
        };

        MapWebView.Navigating += OnMapNavigating;
        MapWebView.Navigated += (_, _) =>
        {
            _ready = true;
            if (_pendingUpdate)
            {
                _pendingUpdate = false;
                UpdateMapAsync(_pendingFit).FireAndForget();
                _pendingFit = false;
            }
        };
    }

    /// <summary>
    /// The map's JavaScript reports a long press by navigating to travelplanner://addplace?lat=..&amp;lon=..;
    /// that "navigation" is cancelled here and turned into "add a place at this point".
    /// </summary>
    private async void OnMapNavigating(object? sender, WebNavigatingEventArgs e)
    {
        if (!MapViewModel.TryParseAddPlaceUrl(e.Url, out var latitude, out var longitude))
            return;

        e.Cancel = true;
        try
        {
            await _viewModel.AddPlaceAtAsync(latitude, longitude);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MapPage] Add place from map failed: {ex}");
        }
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        // async void is unavoidable for a lifecycle override - never let an exception escape it (TP-106).
        try
        {
            // coming back (e.g. from the place form): the map is already on screen - let the back animation finish
            if (_rendered)
                await Task.Delay(PageLoading.TransitionDelay);

            await _viewModel.LoadPlacesCommand.ExecuteAsync(null);

            var dark = IsDark;
            if (!_rendered || dark != _renderedDark)
            {
                await EnsureMapLibreLoadedAsync();
                await RenderMapAsync(dark);
            }
            else
            {
                await UpdateMapAsync(fit: false); // keep where the user was looking
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MapPage] Failed to render map: {ex}");
            await DisplayAlertAsync(AppResources.Common_Error, AppResources.Map_RenderError, AppResources.Common_Ok);
        }
    }

    private static bool IsDark => Application.Current?.RequestedTheme == AppTheme.Dark;

    private string MarkersJson() => JsonSerializer.Serialize(_viewModel.Pins.Select(p => new
    {
        id = p.Id,
        lat = p.Latitude,
        lon = p.Longitude,
        name = p.Name,
        visited = p.IsVisited
    }));

    // TP-505: the selected day's stops, in order, for the route line and numbered markers.
    private string RouteJson() =>
        JsonSerializer.Serialize(_viewModel.RoutePlaces.Select(p => new { id = p.Id, lat = p.Latitude, lon = p.Longitude }));

    /// <summary>New pins / route into the loaded map, without reloading it (no flash, no tile reload).</summary>
    private async Task UpdateMapAsync(bool fit)
    {
        if (!_ready)
        {
            _pendingUpdate = true;
            _pendingFit |= fit;
            return;
        }

        var payload = $"{{\"markers\":{MarkersJson()},\"route\":{RouteJson()}}}";
        var b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));
        try
        {
            await MapWebView.EvaluateJavaScriptAsync($"window.applyDataB64 && window.applyDataB64('{b64}', {(fit ? "true" : "false")})");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MapPage] Map update failed: {ex.Message}");
        }
    }

    private static async Task EnsureMapLibreLoadedAsync()
    {
        _mapLibreJs ??= await ReadAssetAsync("maplibre/maplibre-gl.js");
        _mapLibreCss ??= await ReadAssetAsync("maplibre/maplibre-gl.css");
    }

    private static async Task<string> ReadAssetAsync(string path)
    {
        await using var stream = await FileSystem.OpenAppPackageFileAsync(path);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }

    private async Task RenderMapAsync(bool isDark)
    {
        var lat = _viewModel.CenterLatitude;
        var lon = _viewModel.CenterLongitude;
        var hasCenter = lat != 0 || lon != 0;

        var markersJson = MarkersJson();
        var routeJson = RouteJson();
        var longPressHint = WebUtility.HtmlEncode(AppResources.Map_LongPressHint);

        var initialLat = (hasCenter ? lat : 41.0082).ToString(CultureInfo.InvariantCulture); // falls back to Istanbul
        var initialLon = (hasCenter ? lon : 28.9784).ToString(CultureInfo.InvariantCulture);
        var initialZoom = hasCenter ? 12 : 4;

        var styleUrl = isDark ? DarkStyleUrl : LightStyleUrl;

        // Pin colours follow the theme (same values as the tp-primary / tp-success tokens and the legend):
        // a graphite pin would disappear on the dark map style.
        var pinColor = isDark ? "#F1F2F4" : "#1E2128";
        var pinText = isDark ? "#14161B" : "#FFFFFF";
        var visitedColor = isDark ? "#4CC27E" : "#1B7543";

        // The page is ~1 MB (the map library is inlined): build it off the UI thread.
        var tileError = WebUtility.HtmlEncode(AppResources.Map_TileError); // resources read here: the UI thread has the app's language
        var mapLibreCss = _mapLibreCss;
        var mapLibreJsRaw = _mapLibreJs ?? string.Empty;
        var html = await Task.Run(() =>
        {
        // "</" inside an inline <script> would end the tag early - escape it defensively.
        var mapLibreJs = mapLibreJsRaw.Replace("</script", "<\\/script", StringComparison.OrdinalIgnoreCase);

        return $$"""
        <!DOCTYPE html>
        <html>
        <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no" />
            <style>{{mapLibreCss}}</style>
            <style>
                html, body, #map { height: 100%; margin: 0; padding: 0; }
                #tile-error {
                    display: none; position: absolute; z-index: 1000; left: 12px; right: 12px; top: 12px;
                    padding: 10px 12px; border-radius: 10px; background: #FFF3E0; color: #A15C00;
                    font: 13px system-ui, sans-serif; border: 1px solid #F2DDBA;
                }
                .pin {
                    width: 18px; height: 18px; border-radius: 50%; border: 2px solid #ffffff;
                    box-shadow: 0 1px 3px rgba(0,0,0,.35); cursor: pointer;
                    color: #fff; font: 600 11px system-ui, sans-serif; display: flex; align-items: center; justify-content: center;
                }
                .pin.stop { width: 24px; height: 24px; background: #DC4A33 !important; }
                .maplibregl-popup-content { font: 13px system-ui, sans-serif; color: #14161B; border-radius: 8px; padding: 8px 12px; }
                #hint {
                    position: absolute; z-index: 999; left: 50%; bottom: 28px; transform: translateX(-50%);
                    padding: 7px 12px; border-radius: 8px; background: rgba(20,22,27,.85); color: #fff;
                    font: 12px system-ui, sans-serif; white-space: nowrap; pointer-events: none; transition: opacity .6s;
                }
            </style>
        </head>
        <body>
            <div id="map"></div>
            <div id="tile-error">{{tileError}}</div>
            <div id="hint">{{longPressHint}}</div>
            <script>{{mapLibreJs}}</script>
            <script>
                function showTileError() {
                    document.getElementById('tile-error').style.display = 'block';
                }

                var map;
                try {
                    map = new maplibregl.Map({
                        container: 'map',
                        style: '{{styleUrl}}',
                        center: [{{initialLon}}, {{initialLat}}],
                        zoom: {{initialZoom}},
                        attributionControl: { compact: true }
                    });
                } catch (e) {
                    // e.g. WebGL unavailable on a very old WebView
                    showTileError();
                }

                if (map) {
                    var errorShown = false;
                    map.on('error', function () {
                        if (errorShown) return;
                        errorShown = true;
                        showTileError();
                    });

                    // Pins and the selected day's route. The page is loaded once; later changes (another route day,
                    // a place added or ticked as visited) come through applyData from C#, without reloading the map.
                    var markerObjs = [];
                    var pendingRoute = null;

                    function drawRoute(route) {
                        var data = {
                            type: 'Feature',
                            geometry: { type: 'LineString', coordinates: route.length >= 2 ? route.map(function (r) { return [r.lon, r.lat]; }) : [] }
                        };
                        var source = map.getSource('route');
                        if (source) { source.setData(data); return; }
                        map.addSource('route', { type: 'geojson', data: data });
                        map.addLayer({
                            id: 'route-line',
                            type: 'line',
                            source: 'route',
                            layout: { 'line-join': 'round', 'line-cap': 'round' },
                            paint: { 'line-color': '#DC4A33', 'line-width': 4, 'line-opacity': 0.9 }
                        });
                    }

                    function applyData(markers, route, fit) {
                        markerObjs.forEach(function (m) { m.remove(); });
                        markerObjs = [];

                        var stopNumber = {};
                        route.forEach(function (r, i) { if (!(r.id in stopNumber)) stopNumber[r.id] = i + 1; });
                        var bounds = new maplibregl.LngLatBounds();

                        markers.forEach(function (m) {
                            // Colour = visit status (TP-505): visited green, planned graphite. Stops of the selected day's
                            // route also show their order number.
                            var el = document.createElement('div');
                            el.className = 'pin';
                            el.style.background = m.visited ? '{{visitedColor}}' : '{{pinColor}}';
                            el.style.color = m.visited ? '#FFFFFF' : '{{pinText}}';
                            if (m.id in stopNumber) {
                                el.className = 'pin stop';
                                el.textContent = stopNumber[m.id];
                            }

                            var label = document.createElement('span');
                            label.textContent = m.name;

                            markerObjs.push(new maplibregl.Marker({ element: el })
                                .setLngLat([m.lon, m.lat])
                                .setPopup(new maplibregl.Popup({ offset: 14 }).setDOMContent(label))
                                .addTo(map));

                            bounds.extend([m.lon, m.lat]);
                        });

                        // the route line needs the map style: draw it now or once the style has loaded
                        if (map.isStyleLoaded()) drawRoute(route);
                        else { pendingRoute = route; }

                        if (!fit) return;
                        if (route.length >= 2) {
                            var routeBounds = new maplibregl.LngLatBounds();
                            route.forEach(function (r) { routeBounds.extend([r.lon, r.lat]); });
                            map.fitBounds(routeBounds, { padding: 50, maxZoom: 15, animate: false });
                        } else if (markers.length > 1) {
                            map.fitBounds(bounds, { padding: 40, maxZoom: 15, animate: false });
                        } else if (markers.length === 1) {
                            map.jumpTo({ center: [markers[0].lon, markers[0].lat], zoom: 14 });
                        }
                    }

                    map.on('load', function () {
                        if (pendingRoute) { drawRoute(pendingRoute); pendingRoute = null; }
                    });

                    // C# sends the data as base64 UTF-8 JSON: nothing in a place name can break the script.
                    window.applyDataB64 = function (b64, fit) {
                        var bytes = Uint8Array.from(atob(b64), function (c) { return c.charCodeAt(0); });
                        var d = JSON.parse(new TextDecoder().decode(bytes));
                        applyData(d.markers, d.route, fit);
                    };

                    applyData({{markersJson}}, {{routeJson}}, true);

                    // Long press (touch) or right-click (desktop) -> "add a place here" (TP-505). Implemented with
                    // our own timer because not every WebView turns a long touch into a contextmenu event.
                    var pressTimer = null, pressStart = null, lastReport = 0;
                    function reportPoint(lngLat) {
                        var now = Date.now();
                        if (now - lastReport < 800) return; // touch + contextmenu can both fire
                        lastReport = now;
                        if (navigator.vibrate) { try { navigator.vibrate(30); } catch (e) {} }
                        window.location.href = 'travelplanner://addplace?lat=' + lngLat.lat.toFixed(6) + '&lon=' + lngLat.lng.toFixed(6);
                    }
                    function cancelPress() { if (pressTimer) { clearTimeout(pressTimer); pressTimer = null; } }

                    map.on('touchstart', function (e) {
                        cancelPress();
                        if (!e.originalEvent.touches || e.originalEvent.touches.length !== 1) return;
                        pressStart = e.point;
                        var lngLat = e.lngLat;
                        pressTimer = setTimeout(function () { pressTimer = null; reportPoint(lngLat); }, 650);
                    });
                    map.on('touchmove', function (e) {
                        if (!pressStart) return;
                        var dx = e.point.x - pressStart.x, dy = e.point.y - pressStart.y;
                        if (dx * dx + dy * dy > 100) cancelPress();
                    });
                    map.on('touchend', cancelPress);
                    map.on('touchcancel', cancelPress);
                    map.on('movestart', function (e) { if (e.originalEvent) cancelPress(); });
                    map.on('contextmenu', function (e) { cancelPress(); reportPoint(e.lngLat); });

                    setTimeout(function () { document.getElementById('hint').style.opacity = '0'; }, 4000);
                }
            </script>
        </body>
        </html>
        """;
        });

        _ready = false;
        _pendingUpdate = false;
        _pendingFit = false;
        MapWebView.Source = new HtmlWebViewSource { Html = html };
        _rendered = true;
        _renderedDark = isDark;
    }
}
