import './style.css';
import { bookingMailto, checkBooking, site } from './site';

/** Escape text before it goes into markup. Content comes from site.json; escape it anyway. */
function esc(value: string): string {
  return value.replace(/[&<>"']/g, (char) => `&#${char.charCodeAt(0)};`);
}

function render(): string {
  const gallery = site.gallery
    .map((item) => `<li class="tile tone-${esc(item.tone)}"><span>${esc(item.title)}</span></li>`)
    .join('');
  const services = site.services
    .map((service) => `<article class="card"><h3>${esc(service.title)}</h3><p>${esc(service.body)}</p></article>`)
    .join('');
  const hours = site.hours.map((row) => `<div><dt>${esc(row.days)}</dt><dd>${esc(row.time)}</dd></div>`).join('');
  const tel = site.phone.replace(/\s+/g, '');
  return `
  <header class="top">
    <a class="brand" href="#top">${esc(site.name)}</a>
    <nav aria-label="Main">
      <a href="#work">Work</a><a href="#services">Services</a><a href="#about">About</a><a class="cta" href="#book">Book</a>
    </nav>
  </header>
  <main id="main">
    <section class="hero" id="top">
      <p class="eyebrow">// ${esc(site.name)}</p>
      <h1>${esc(site.tagline)}</h1>
      <a class="button" href="#book">Request a booking</a>
    </section>
    <section id="work" aria-labelledby="work-title">
      <p class="eyebrow">// Work</p>
      <h2 id="work-title">Recent pieces</h2>
      <ul class="gallery">${gallery}</ul>
    </section>
    <section id="services" aria-labelledby="services-title">
      <p class="eyebrow">// Services</p>
      <h2 id="services-title">What we do</h2>
      <div class="cards">${services}</div>
    </section>
    <section id="about" aria-labelledby="about-title">
      <p class="eyebrow">// About</p>
      <h2 id="about-title">The studio</h2>
      <p class="lead">${esc(site.about)}</p>
      <dl class="hours">${hours}</dl>
    </section>
    <section id="book" aria-labelledby="book-title">
      <p class="eyebrow">// Booking</p>
      <h2 id="book-title">Request a booking</h2>
      <form class="booking" novalidate>
        <label>Name<input name="name" autocomplete="name" required /></label>
        <label>Email<input name="email" type="email" autocomplete="email" required /></label>
        <label>Your idea<textarea name="idea" rows="4" required></textarea></label>
        <label>Placement<input name="placement" placeholder="Forearm, back, not sure" /></label>
        <label>Preferred date<input name="preferredDate" type="date" /></label>
        <p class="status" role="status" aria-live="polite"></p>
        <button class="button" type="submit">Send request</button>
      </form>
    </section>
  </main>
  <footer class="foot">
    <p>${esc(site.address)}</p>
    <p><a href="mailto:${esc(site.email)}">${esc(site.email)}</a> &middot; <a href="tel:${esc(tel)}">${esc(site.phone)}</a></p>
    <p class="small">&copy; ${new Date().getFullYear()} ${esc(site.name)}</p>
  </footer>`;
}

function wireBooking(form: HTMLFormElement): void {
  const status = form.querySelector<HTMLParagraphElement>('.status');
  const say = (text: string) => {
    if (status) status.textContent = text;
  };
  form.addEventListener('submit', async (event) => {
    event.preventDefault();
    const checked = checkBooking(Object.fromEntries(new FormData(form)));
    if (!checked.ok) {
      say(checked.problems.join(' '));
      return;
    }
    if (site.bookingEndpoint.length === 0) {
      window.location.href = bookingMailto(site.email, checked.request);
      say('Your email app opened with the request filled in.');
      return;
    }
    try {
      const response = await fetch(site.bookingEndpoint, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(checked.request),
      });
      if (!response.ok) throw new Error(String(response.status));
      form.reset();
      say('Thanks. We will reply within two days.');
    } catch {
      say(`That did not go through. Email us at ${site.email} instead.`);
    }
  });
}

const root = document.querySelector<HTMLDivElement>('#app');
if (root) {
  root.innerHTML = render();
  const form = root.querySelector<HTMLFormElement>('form.booking');
  if (form) wireBooking(form);
}
