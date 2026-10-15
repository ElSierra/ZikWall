# Map and location data

## Offline shaded relief (October 2026 map redesign)

The viewport uses a 2048 × 1024 Natural Earth I shaded-relief texture, with a
dark cartographic palette applied at build time. It is bundled locally; panning
and zooming never fetch tiles or disclose connection IPs. This is geographic
relief, not live satellite imagery. Country markers remain approximate country
centers, not city or server positions. The device hub is schematic.

Source (public domain): https://naturalearth.s3.amazonaws.com/50m_raster/NE1_50M_SR_W.zip

Archive SHA-256: `9e85417223414bbed425aea5dca0f0b4c5661fc94d4f14494140a21a08dfa450`

Generated `Assets/EarthRelief.png` SHA-256:
`c326ab2b4ec75e792e118ebb16a7abd3e4a80cbc36e42534a2410b2fbe17290d`

Reproduce with `python tools/build-map-relief.py temp/natural-earth-relief.zip`
(Pillow required). Natural Earth terms: https://www.naturalearthdata.com/about/terms-of-use/

## Original vector outline

The static equirectangular land outline uses Natural Earth 1:110m land polygons, version 5.1.2. Natural Earth data is public domain: https://www.naturalearthdata.com/about/terms-of-use/ .

Source: https://raw.githubusercontent.com/nvkelso/natural-earth-vector/v5.1.2/geojson/ne_110m_land.geojson

SHA-256: `9E0729EE253CA7D7A5C4AE9395FB1902264C5377C52E224D13DD85010E2835D9`

`tools/generate-world-map.py` checks that hash before generating the committed SVG. The application does not download map tiles or send connection IPs to a geolocation service.

Locations require a user-supplied CSV with this exact header:

```csv
network,latitude,longitude,label
192.0.2.0/24,0,0,Documentation example only
```

The example uses a reserved documentation network and is not a real location claim. Use a suitably licensed, current IP geolocation dataset and convert it to these fields. Maximum input: 8 MB / 50,000 networks; IPv4 and IPv6 CIDRs are supported with longest-prefix matching. Unknown IPs are left unlocated. Imported data is copied into the user's local ZikWall application data directory. Coordinates are approximate and cannot establish a person's physical location.

## Bundled country positions

Country markers now work without a CSV import. Label positions (approximate country centers, never server coordinates) come from Natural Earth 5.1.2, public domain. Countries without a position remain unlocated.

Source: https://raw.githubusercontent.com/nvkelso/natural-earth-vector/v5.1.2/geojson/ne_50m_admin_0_countries.geojson

Input SHA-256: `3e458fc036ad0a66411f2c1e6cac49c5d7bfb81cb1123bc513b22511a2b7fdeb`

Output SHA-256: `55aa362bada3ef3439dfee9920900f85317467e47f789a1cc525d1cfef1242a6`

When several geographic features share a country code, use the most populous feature (e.g. mainland Australia, not Ashmore and Cartier Islands).
