import type { ElementRef } from '@angular/core';

/** iter-38 S8 NG-R13: isolate direct DOM querySelector behind a small wrapper, used by chart components. */
export function queryHost(el: ElementRef, selector: string): HTMLElement | null {
  return el.nativeElement.querySelector(selector);
}
