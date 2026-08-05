import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { API } from '../api-base';
import { Onboarding } from '../modelos';

/** Os primeiros passos da empresa.
 *
 *  O estado fica num signal COMPARTILHADO: o shell precisa dele para decidir se mostra o link
 *  "Primeiros passos", e a tela precisa dele para desenhar o checklist. Duas cópias divergiriam
 *  — o link continuaria aceso depois de a tela marcar o último passo. */
@Injectable({ providedIn: 'root' })
export class OnboardingServico {
  private http = inject(HttpClient);

  readonly estado = signal<Onboarding | null>(null);

  carregar(): Observable<Onboarding> {
    return this.http.get<Onboarding>(`${API}/onboarding`).pipe(
      tap(o => this.estado.set(o))
    );
  }

  /** "Convido a equipe depois." */
  dispensarEquipe(): Observable<void> {
    return this.http.post<void>(`${API}/onboarding/equipe/dispensar`, {});
  }

  /** Fecha o painel de vez. */
  dispensar(): Observable<void> {
    return this.http.post<void>(`${API}/onboarding/dispensar`, {});
  }
}
