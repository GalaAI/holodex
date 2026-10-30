import data from './site.json';

/** The site's content. Edit site.json; the page renders from it. */
export interface Service {
  title: string;
  body: string;
}

export interface GalleryItem {
  title: string;
  tone: string;
}

export interface Site {
  name: string;
  tagline: string;
  email: string;
  phone: string;
  address: string;
  /** Where the booking form posts (JSON). Empty means it opens an email instead. */
  bookingEndpoint: string;
  hours: Array<{ days: string; time: string }>;
  services: Service[];
  gallery: GalleryItem[];
  about: string;
}

export const site: Site = data;

export interface BookingRequest {
  name: string;
  email: string;
  idea: string;
  placement: string;
  preferredDate: string;
}

export type BookingCheck = { ok: true; request: BookingRequest } | { ok: false; problems: string[] };

const EMAIL = /^[^\s@]+@[^\s@]+\.[^\s@]+$/;

/** Validate a booking form before it leaves the page. Plain words for every problem. */
export function checkBooking(input: Record<string, FormDataEntryValue | null | undefined>): BookingCheck {
  const text = (key: string): string => {
    const value = input[key];
    return typeof value === 'string' ? value.trim() : '';
  };
  const request: BookingRequest = {
    name: text('name'),
    email: text('email'),
    idea: text('idea'),
    placement: text('placement'),
    preferredDate: text('preferredDate'),
  };
  const problems: string[] = [];
  if (request.name.length === 0) problems.push('Add your name.');
  if (!EMAIL.test(request.email)) problems.push('Add an email we can reply to.');
  if (request.idea.length < 10) problems.push('Tell us a little more about your idea.');
  return problems.length === 0 ? { ok: true, request } : { ok: false, problems };
}

/** A mailto link that carries the booking request, for sites without a booking endpoint. */
export function bookingMailto(to: string, request: BookingRequest): string {
  const body = [
    `Name: ${request.name}`,
    `Email: ${request.email}`,
    `Placement: ${request.placement || 'Not sure yet'}`,
    `Preferred date: ${request.preferredDate || 'Flexible'}`,
    '',
    request.idea,
  ].join('\n');
  const subject = `Booking request from ${request.name}`;
  return `mailto:${encodeURIComponent(to)}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`;
}
