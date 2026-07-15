import { signal, Directive } from '@angular/core';

@Directive()
export abstract class DetailFormBase {
  saving = signal(false);
  savedOk = signal(false);
  abstract save(): Promise<void>;
}
