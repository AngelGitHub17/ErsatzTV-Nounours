# ErsatzTV — édition Nounours

Fork d'ErsatzTV Legacy (v26.5.1) repensé : interface en français, tableau de bord, assistant de chaîne,
**programmes composés** (pubs → jingle → programme coupé par des pubs → pubs), planning visuel,
studio d'habillage façon OBS avec horloge en direct, et système de modules.

- Ce qui change et comment s'en servir : [NOUNOURS.md](NOUNOURS.md)
- Créer un module : [MODULES.md](MODULES.md)

**Compiler et lancer** : ouvrir `ErsatzTV.sln` dans Visual Studio (SDK .NET 10) et démarrer le projet `ErsatzTV`,
ou `dotnet run --project ErsatzTV -c Release`, puis ouvrir http://localhost:8409.

---

## ErsatzTV (projet d'origine)

ErsatzTV lets you transform your media library into a personalized, live TV experience - complete with EPG, channel scheduling, and seamless streaming to all your devices. Rediscover your content, your way.

[![contact](https://img.shields.io/badge/contact_us-510b80?style=for-the-badge)](https://ersatztv.org/contact)
[![features](https://img.shields.io/badge/vote_on_features-darkgreen?style=for-the-badge)](https://features.ersatztv.org/)
[![community](https://img.shields.io/badge/join_the_community-blue?style=for-the-badge)](https://discuss.ersatztv.org)

![epg-example](https://ersatztv.org/images/home/epg-example.png)

## How It Works

1. **Install ErsatzTV**: Download and set up the server on your system.
2. **Add Your Media**: Connect your media libraries and collections.
3. **Create Channels**: Design and schedule your own live channels.
4. **Stream Anywhere**: Watch on any device with IPTV and EPG support.

## Key Features

- **Custom channels**: Create and schedule your own live TV channels.
- **IPTV & EPG**: Stream with IPTV and Electronic Program Guide support.
- **Hardware Transcoding**: High-performance streaming with hardware acceleration (NVENC, QSV, VAAPI, AMF, VideoToolbox)
- **Media Server Integration**: Connect Plex, Jellyfin, Emby and more.
- **Music & Subtitles**: Mix music videos and enjoy subtitle support.
- **Open Source**: Free, open, and community-driven project.

## Documentation

Documentation is available at [ersatztv.org](https://ersatztv.org/docs/).

## License

This project is inspired by [pseudotv-plex](https://github.com/DEFENDORe/pseudotv) and
the [dizquetv](https://github.com/vexorian/dizquetv) fork and is released under the [zlib license](LICENSE).
