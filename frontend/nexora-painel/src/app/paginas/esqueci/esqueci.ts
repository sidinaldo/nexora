import { Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { EquipeServico } from '../../nucleo/servicos/equipe.servico';

/** "Esqueci minha senha" — tela PÚBLICA, sem sessão.
 *
 *  ===================== A TELA TAMBÉM NÃO PODE DEDURAR =====================
 *  O servidor responde igual exista o e-mail ou não; a tela precisa fazer o mesmo. Por isso não
 *  há caminho de erro "e-mail não encontrado" — o único desfecho de sucesso é a mesma mensagem,
 *  redigida para ser verdadeira nos dois casos ("se houver uma conta com esse e-mail").
 *
 *  Nem servidor nem tela afirmam que enviaram. Dizer "enviamos" quando não existe conta seria
 *  mentira; dizer "não existe" seria entregar a lista de clientes a quem estiver testando
 *  endereços.
 *  ========================================================================== */
@Component({
  selector: 'app-esqueci',
  imports: [FormsModule, RouterLink],
  templateUrl: './esqueci.html',
  styleUrl: './esqueci.css'
})
export class Esqueci {
  private servico = inject(EquipeServico);

  email = signal('');
  enviando = signal(false);
  enviado = signal(false);
  erro = signal('');

  solicitar() {
    const alvo = this.email().trim();
    if (!alvo) return;

    this.enviando.set(true);
    this.erro.set('');

    this.servico.solicitarReset(alvo).subscribe({
      next: () => { this.enviando.set(false); this.enviado.set(true); },
      error: e => {
        this.enviando.set(false);
        // O único erro possível aqui é 429 (rate limit) ou queda da API. NUNCA "não encontrado":
        // o servidor responde 200 mesmo quando a conta não existe.
        this.erro.set(e.error?.erro ?? 'Não foi possível concluir. Tente novamente em instantes.');
      }
    });
  }
}
