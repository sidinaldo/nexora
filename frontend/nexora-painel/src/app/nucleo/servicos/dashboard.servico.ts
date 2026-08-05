import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import {
  AgrupamentoSerie, DashboardDto, PaginaAtividades, SerieTemporalDto
} from '../modelos';

/** O payload RICO, sob demanda — UMA vez, quando a página abre.
 *
 *  Não confundir com o PainelServico: aquele é o barato, que o shell faz polling de 45s.
 *  Colocar o funil no polling seria pagar a agregação a cada 45 segundos por usuário. */
@Injectable({ providedIn: 'root' })
export class DashboardServico {
  private http = inject(HttpClient);

  dashboard(): Observable<DashboardDto> {
    return this.http.get<DashboardDto>(`${API}/dashboard`);
  }

  /** A evolução no período. Agregada no SQL; a tela só desenha. */
  serie(de: string, ate: string, agrupamento: AgrupamentoSerie): Observable<SerieTemporalDto> {
    const p = new HttpParams().set('de', de).set('ate', ate).set('agrupamento', agrupamento);
    return this.http.get<SerieTemporalDto>(`${API}/dashboard/serie`, { params: p });
  }

  /** Atividade recente, por cursor.
   *
   *  O recorte por papel é da API: o Vendedor recebe só o que é dele. Não há filtro a fazer
   *  aqui — se houvesse, o dado dos outros já teria chegado ao navegador. */
  atividades(cursorEm?: string | null, cursorChave?: string | null,
             responsavelId?: number | null, tamanho = 20): Observable<PaginaAtividades> {
    let p = new HttpParams().set('tamanho', tamanho);
    if (cursorEm) p = p.set('cursorEm', cursorEm);
    if (cursorChave) p = p.set('cursorChave', cursorChave);
    if (responsavelId != null) p = p.set('responsavelId', responsavelId);
    return this.http.get<PaginaAtividades>(`${API}/dashboard/atividades`, { params: p });
  }

  // O `demo()` foi removido junto com `/api/dashboard/demo`: a demonstração agora é um tenant
  // com dados reais no banco, não um payload gerado. Ver docs/PI-4b.md.
}
