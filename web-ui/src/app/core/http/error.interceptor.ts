import { HttpInterceptorFn, HttpErrorResponse } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';

/**
 * iter-38 S7 NG-R3 / W-D1: surface HTTP errors so silent catch blocks don't hide 500s behind empty states.
 * Components should still handle errors gracefully; this interceptor ensures the error is logged + propagated.
 */
export const errorInterceptor: HttpInterceptorFn = (req, next) => {
  return next(req).pipe(
    catchError((err: unknown) => {
      if (err instanceof HttpErrorResponse) {
        console.error(`[HTTP ${err.status}] ${req.method} ${req.url}`, err.message, err.error);
      } else {
        console.error(`[HTTP] ${req.method} ${req.url}`, err);
      }
      return throwError(() => err);
    }),
  );
};
