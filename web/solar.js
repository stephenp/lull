// NOAA solar position algorithm (shared by index.html; also loadable in node for tests)
(function (root) {
  const rad = Math.PI / 180, deg = 180 / Math.PI;

  function core(ms) {
    const jd = ms / 86400000 + 2440587.5;
    const T = (jd - 2451545) / 36525;
    const L0 = ((280.46646 + T * (36000.76983 + T * 0.0003032)) % 360 + 360) % 360;
    const M = 357.52911 + T * (35999.05029 - 0.0001537 * T);
    const e = 0.016708634 - T * (0.000042037 + 0.0000001267 * T);
    const C = Math.sin(M * rad) * (1.914602 - T * (0.004817 + 0.000014 * T)) +
      Math.sin(2 * M * rad) * (0.019993 - 0.000101 * T) + Math.sin(3 * M * rad) * 0.000289;
    const trueLong = L0 + C;
    const omega = 125.04 - 1934.136 * T;
    const appLong = trueLong - 0.00569 - 0.00478 * Math.sin(omega * rad);
    const meanObliq = 23 + (26 + (21.448 - T * (46.815 + T * (0.00059 - T * 0.001813))) / 60) / 60;
    const obliq = meanObliq + 0.00256 * Math.cos(omega * rad);
    const decl = Math.asin(Math.sin(obliq * rad) * Math.sin(appLong * rad)) * deg;
    const y = Math.pow(Math.tan(obliq * rad / 2), 2);
    const eqTime = 4 * deg * (y * Math.sin(2 * L0 * rad) - 2 * e * Math.sin(M * rad) +
      4 * e * y * Math.sin(M * rad) * Math.cos(2 * L0 * rad) -
      0.5 * y * y * Math.sin(4 * L0 * rad) - 1.25 * e * e * Math.sin(2 * M * rad));
    return { decl, eqTime };
  }

  // altitude/azimuth in degrees (azimuth from north, clockwise)
  function position(ms, lat, lon) {
    const { decl, eqTime } = core(ms);
    const d = new Date(ms);
    const utcMin = d.getUTCHours() * 60 + d.getUTCMinutes() + d.getUTCSeconds() / 60 + d.getUTCMilliseconds() / 60000;
    let tst = ((utcMin + eqTime + 4 * lon) % 1440 + 1440) % 1440;
    const ha = tst / 4 < 0 ? tst / 4 + 180 : tst / 4 - 180;
    const cosZ = Math.sin(lat * rad) * Math.sin(decl * rad) + Math.cos(lat * rad) * Math.cos(decl * rad) * Math.cos(ha * rad);
    const zen = Math.acos(Math.max(-1, Math.min(1, cosZ))) * deg;
    let alt = 90 - zen;
    // atmospheric refraction (NOAA approximation)
    if (alt > -0.575) {
      const te = Math.tan(alt * rad);
      let r;
      if (alt > 85) r = 0;
      else if (alt > 5) r = 58.1 / te - 0.07 / (te * te * te) + 0.000086 / Math.pow(te, 5);
      else r = 1735 + alt * (-518.2 + alt * (103.4 + alt * (-12.79 + alt * 0.711)));
      alt += r / 3600;
    } else {
      alt += (-20.774 / Math.tan(alt * rad)) / 3600;
    }
    const az = (Math.atan2(Math.sin(ha * rad),
      Math.cos(ha * rad) * Math.sin(lat * rad) - Math.tan(decl * rad) * Math.cos(lat * rad)) * deg + 180 + 360) % 360;
    return { altitude: alt, azimuth: az, declination: decl };
  }

  // Sunrise/sunset (epoch ms) for the UTC day containing `dayMs` local noon.
  // Returns {sunrise, sunset, noon, polar: null|'day'|'night'}
  function times(dayMs, lat, lon, zenithDeg) {
    zenithDeg = zenithDeg || 90.833;
    // local calendar date -> approximate solar noon epoch
    const ld = new Date(dayMs);
    const dayStartUTC = Date.UTC(ld.getFullYear(), ld.getMonth(), ld.getDate());
    function event(sign) {
      // iterate twice for accuracy
      let guess = dayStartUTC + 12 * 3600000 - lon * 4 * 60000;
      let result = null, polar = null;
      for (let i = 0; i < 3; i++) {
        const { decl, eqTime } = core(guess);
        const noonMin = 720 - 4 * lon - eqTime;
        const arg = Math.cos(zenithDeg * rad) / (Math.cos(lat * rad) * Math.cos(decl * rad)) - Math.tan(lat * rad) * Math.tan(decl * rad);
        if (sign === 0) { result = dayStartUTC + noonMin * 60000; guess = result; continue; }
        if (arg > 1) { polar = 'night'; result = null; break; }
        if (arg < -1) { polar = 'day'; result = null; break; }
        const haS = Math.acos(arg) * deg;
        result = dayStartUTC + (noonMin + sign * 4 * haS) * 60000;
        guess = result;
      }
      return { t: result, polar };
    }
    const r = event(-1), s = event(1), n = event(0);
    // local date boundary: the noon estimate is anchored on the UTC date of the local date;
    // for far east/west longitudes, shift by a day so noon lands on the local date.
    let shift = 0;
    const localNoonDate = new Date(n.t);
    if (localNoonDate.getDate() !== ld.getDate()) shift = (localNoonDate < ld ? 1 : -1) * 86400000;
    const add = (v) => v == null ? null : v + shift;
    return { sunrise: add(r.t), sunset: add(s.t), noon: add(n.t), polar: r.polar || s.polar };
  }

  const api = { position, times };
  if (typeof module !== 'undefined') module.exports = api; else root.SunMath = api;
})(this);
