import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { EtiquetaDto } from '../modelos';

/** O vocabulário de etiquetas da empresa.
 *
 *  ⚠️ LER e ESCREVER têm públicos diferentes, e a API separa por ação:
 *
 *    `listar`  — qualquer papel. É a lista de onde o vendedor escolhe qual aplicar.
 *    o resto   — só o dono. Criar etiqueta é configuração; a API devolve 403 para os outros.
 *
 *  Diferente do `EtapasServico`, em que tudo é do dono. */
@Injectable({ providedIn: 'root' })
export class EtiquetasServico {
  private http = inject(HttpClient);

  listar(): Observable<EtiquetaDto[]> {
    return this.http.get<EtiquetaDto[]>(`${API}/etiquetas`);
  }

  criar(nome: string, cor: string | null): Observable<{ id: number }> {
    return this.http.post<{ id: number }>(`${API}/etiquetas`, { nome, cor });
  }

  atualizar(id: number, nome: string, cor: string | null): Observable<void> {
    return this.http.put<void>(`${API}/etiquetas/${id}`, { nome, cor });
  }

  /** Sem `destino`, diferente de etapa: apagar etiqueta não deixa contato órfão — ele só perde
   *  um rótulo. Etapa é coluna do kanban, e sem ela o card não tem onde ficar. */
  remover(id: number): Observable<void> {
    return this.http.delete<void>(`${API}/etiquetas/${id}`);
  }
}
