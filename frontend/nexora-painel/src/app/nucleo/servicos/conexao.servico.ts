import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import {
  CanalWhatsapp, Conexao, Conexoes, ModeloMensagem, NovaConexao, NovoModelo, QrCode, SaudeConexao,
  StatusConexaoDto, TesteConexao
} from '../modelos';

/** Os números de WhatsApp da empresa. Quantos ela pode ter vem do plano, e o servidor é quem
 *  diz — ver `Conexoes.limite`. */
@Injectable({ providedIn: 'root' })
export class ConexaoServico {
  private http = inject(HttpClient);
  private readonly base = `${API}/conexoes`;

  listar(): Observable<Conexoes> {
    return this.http.get<Conexoes>(this.base);
  }

  /** A mesma lista, depois de o servidor conferir cada número na Evolution e corrigir o que o
   *  banco dizia errado. Uma chamada só, ao abrir a tela — não é polling. */
  conferir(): Observable<Conexoes> {
    return this.http.post<Conexoes>(`${this.base}/conferir`, {});
  }

  obter(id: number): Observable<Conexao> {
    return this.http.get<Conexao>(`${this.base}/${id}`);
  }

  criar(nova: NovaConexao): Observable<{ id: number }> {
    return this.http.post<{ id: number }>(this.base, nova);
  }

  /** Só o nome. `instanceName` não tem rota de edição em lugar nenhum, de propósito: é a
   *  identidade na Evolution e a chave pela qual o webhook acha o tenant. */
  renomear(id: number, nome: string): Observable<void> {
    return this.http.put<void>(`${this.base}/${id}`, { nome });
  }

  remover(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/${id}`);
  }

  /** Estado ao vivo na Evolution. A tela chama em polling de 3s enquanto o QR está na frente
   *  do usuário — é assim que ela descobre que o pareamento deu certo. */
  status(id: number): Observable<StatusConexaoDto> {
    return this.http.get<StatusConexaoDto>(`${this.base}/${id}/status`);
  }

  conectar(id: number): Observable<QrCode> {
    return this.http.post<QrCode>(`${this.base}/${id}/conectar`, {});
  }

  parear(id: number, numero: string): Observable<QrCode> {
    return this.http.post<QrCode>(`${this.base}/${id}/parear`, { numero });
  }

  desconectar(id: number): Observable<void> {
    return this.http.post<void>(`${this.base}/${id}/desconectar`, {});
  }

  reconhecerTroca(id: number): Observable<void> {
    return this.http.post<void>(`${this.base}/${id}/reconhecer-troca`, {});
  }

  saude(id: number): Observable<SaudeConexao> {
    return this.http.get<SaudeConexao>(`${this.base}/${id}/saude`);
  }

  // ---------------------------------------------------------------- API oficial (INT-XX)
  /** Vazio (null) mantém o que está guardado: a tela nunca recebe o valor. */
  atualizarCredenciais(id: number, accessToken: string | null, appSecret: string | null): Observable<void> {
    return this.http.put<void>(`${this.base}/${id}/credenciais`, { accessToken, appSecret });
  }

  testar(id: number): Observable<TesteConexao> {
    return this.http.post<TesteConexao>(`${this.base}/${id}/testar`, {});
  }

  // ---------------------------------------------------------------- templates (INT-XX)
  listarModelos(conexaoId: number): Observable<ModeloMensagem[]> {
    return this.http.get<ModeloMensagem[]>(`${this.base}/${conexaoId}/modelos`);
  }

  /** Cria o RASCUNHO. Nada vai à Meta ainda. */
  criarModelo(conexaoId: number, novo: NovoModelo): Observable<{ id: number }> {
    return this.http.post<{ id: number }>(`${this.base}/${conexaoId}/modelos`, novo);
  }

  /** Só o rascunho: depois de enviado, o texto é o que a Meta revisou. */
  editarModelo(id: number, novo: NovoModelo): Observable<void> {
    return this.http.put<void>(`${API}/modelos/${id}`, novo);
  }

  excluirModelo(id: number): Observable<void> {
    return this.http.delete<void>(`${API}/modelos/${id}`);
  }

  /** Manda para a revisão da Meta. */
  submeterModelo(id: number): Observable<ModeloMensagem> {
    return this.http.post<ModeloMensagem>(`${API}/modelos/${id}/enviar`, {});
  }

  /** Pergunta à Meta como está a revisão, agora. */
  atualizarModelo(id: number): Observable<ModeloMensagem> {
    return this.http.post<ModeloMensagem>(`${API}/modelos/${id}/atualizar`, {});
  }

  definirCanalPadrao(canal: CanalWhatsapp): Observable<void> {
    return this.http.put<void>(`${this.base}/canal-padrao`, { canal });
  }
}
